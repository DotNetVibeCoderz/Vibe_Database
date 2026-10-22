"""Tests for the query builder.

The golden cases are shared with the Go and Node builders through
``clients/querybuilder-golden.json``; the .NET suite parses and runs the same file, so agreeing with
it means agreeing with the real parser too.

Set ``CUTEDB_TEST_SERVER`` (e.g. http://127.0.0.1:8420) to also run every golden case end to end
against a live server. That test drops and recreates the ``qb_golden`` collection.

Run with: python -m pytest, or python -m unittest discover tests
"""

from __future__ import annotations

import json
import os
import sys
import unittest
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path
from typing import Any, Callable
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from cutedb import (  # noqa: E402
    CuteClient,
    Query,
    and_,
    escape_like,
    field,
    not_,
    or_,
)
from cutedb import _DecimalEncoder  # noqa: E402

GOLDEN = json.loads(
    (Path(__file__).resolve().parents[2] / "querybuilder-golden.json").read_text(encoding="utf-8")
)
C = GOLDEN["collection"]


def utc(year: int, month: int, day: int) -> datetime:
    return datetime(year, month, day, tzinfo=timezone.utc)


# One builder per golden case, written the way a person would write it.
CASES: dict[str, Callable[[], Query]] = {
    "select_all": lambda: Query(C),
    "eq_string": lambda: Query(C).where(field("address.city") == "Bandung"),
    "ne_and_gt": lambda: Query(C).where(field("status") != "cancelled", field("total") > 200000),
    "ge_and_le": lambda: Query(C).where(field("total") >= 1000).where(field("total") <= Decimal("125000.5")),
    "lt_or_ge": lambda: Query(C).where((field("total") < 2000) | (field("total") >= 900000)),
    "or_inside_and": lambda: Query(C).where(
        field("address.city") == "Bandung",
        or_(field("status") == "cancelled", field("total") < 2000),
    ),
    "not": lambda: Query(C).where(~(field("status") == "paid")),
    "not_or_and_present": lambda: Query(C).where(
        not_(field("address.city").eq("Bandung") | field("address.city").eq("Jakarta")),
        field("total").is_not_missing(),
    ),
    "in_strings": lambda: Query(C).where(field("status").is_in(["shipped", "draft"])),
    "in_numbers": lambda: Query(C).where(field("total").is_in((1000, 3000, 7))),
    "not_in": lambda: Query(C).where(field("address.city").not_in(["Bandung", "Jakarta"])),
    "between": lambda: Query(C).where(field("total").between(100000, 300000)),
    "between_dates": lambda: Query(C).where(field("placedAt").between(utc(2026, 2, 1), utc(2026, 3, 31))),
    "not_between": lambda: Query(C).where(field("total").not_between(1000, 900000)),
    "is_null": lambda: Query(C).where(field("note").is_null()),
    "ne_none_is_not_null": lambda: Query(C).where(field("note") != None),  # noqa: E711
    "is_missing": lambda: Query(C).where(field("total").is_missing()),
    "is_not_missing": lambda: Query(C).where(field("note").is_not_missing()),
    "nested_path": lambda: Query(C).where(field("customer.tier") == "gold"),
    "projecting_path": lambda: Query(C).where(field("lines[].sku") == "NR-02"),
    "indexed_path": lambda: Query(C).where(field("lines[0].qty") > 3),
    "array_elementwise": lambda: Query(C).where(field("tags") == "promo"),
    "array_whole": lambda: Query(C).where(field("tags") == ["promo", "bulk"]),
    "bool_or_decimal": lambda: Query(C).where((field("vip") == True) | (field("total") == 125000.5)),  # noqa: E712
    "startswith_escapes_percent": lambda: Query(C).where(field("code").startswith("100%")),
    "contains_escapes_underscore": lambda: Query(C).where(field("code").contains("%_")),
    "endswith": lambda: Query(C).where(field("customer.name").endswith("--")),
    "like_raw_pattern": lambda: Query(C).where(field("code").like("O_")),
    "not_like": lambda: Query(C).where(field("code").not_like("1%")),
    "injection_or_comment": lambda: Query(C).where(field("customer.name") == "' OR 1=1 --"),
    "injection_quote": lambda: Query(C).where(field("customer.name") == "O'Brien"),
    "injection_backslash": lambda: Query(C).where(field("customer.name") == 'Back\\slash "quoted"'),
    "injection_no_match": lambda: Query(C).where(field("code") == "x' OR '1'='1"),
    "injection_startswith": lambda: Query(C).where(field("customer.name").startswith("' OR 1=1 --")),
    "unicode_and_newline": lambda: Query(C).where(
        and_(field("note") == "line1\nline2", field("customer.name") == "Dewi Ünal 李")
    ),
    "projection_order_limit_offset": lambda: Query(C)
    .select("code", buyer="customer.name")
    .select("total")
    .where(field("status") == "paid")
    .order_by("total", desc=True)
    .limit(2)
    .offset(1),
    "order_by_two_keys": lambda: Query(C)
    .where(field("address.city") == "Bandung")
    .order_by("status")
    .order_by("total", desc=True),
    "order_limit": lambda: Query(C).select("code").order_by("code").limit(3),
    "offset_only": lambda: Query(C).order_by("code").offset(7),
    "select_projecting_path": lambda: Query(C)
    .select("code", skus="lines[].sku")
    .where(field("lines[].sku") == "NR-01"),
}


def as_json(value: Any) -> Any:
    """What the server will receive: the parameters after the client's own JSON encoding."""
    return json.loads(json.dumps(value, cls=_DecimalEncoder, ensure_ascii=False))


class GoldenTests(unittest.TestCase):
    def test_every_golden_case_has_a_builder(self) -> None:
        self.assertEqual(sorted(CASES), sorted(case["name"] for case in GOLDEN["cases"]))

    def test_builders_render_the_golden_text_and_parameters(self) -> None:
        for case in GOLDEN["cases"]:
            with self.subTest(case["name"]):
                text, parameters = CASES[case["name"]]().build()
                self.assertEqual(text, case["cuteql"])
                self.assertEqual(as_json(parameters), case["parameters"])


class InjectionTests(unittest.TestCase):
    HOSTILE = [
        "' OR 1=1 --",
        "x'; DELETE FROM orders --",
        "''",
        "\\'",
        '" OR ""="',
        "/* comment */",
        "@p1",
        "line\nWHERE 1=1",
        "\u0000  Ünal 李 🙂",
    ]

    def test_values_never_reach_the_text(self) -> None:
        for hostile in self.HOSTILE:
            with self.subTest(hostile=hostile):
                query = Query("orders").where(
                    field("name") == hostile,
                    field("name").startswith(hostile),
                    field("tags").is_in([hostile]),
                    field("name").between(hostile, hostile),
                )
                text, parameters = query.build()
                self.assertEqual(
                    text,
                    "SELECT * FROM orders WHERE name = @p0 AND name LIKE @p1 "
                    "AND tags IN @p2 AND name BETWEEN @p3 AND @p4",
                )
                self.assertEqual(parameters["p0"], hostile)
                self.assertEqual(parameters["p2"], [hostile])

    def test_startswith_escapes_like_the_linq_provider(self) -> None:
        # The same three replacements as CuteExpressionTranslator.Like, backslash first.
        self.assertEqual(escape_like("a\\b%c_d"), "a\\\\b\\%c\\_d")
        self.assertEqual(Query("t").where(field("x").startswith("50%_\\")).parameters, {"p0": "50\\%\\_\\\\%"})
        self.assertEqual(Query("t").where(field("x").endswith("_")).parameters, {"p0": "%\\_"})

    def test_hostile_field_names_are_refused(self) -> None:
        for bad in [
            "name = 'x' OR 1=1 --",
            "name;",
            "name)",
            "a b",
            "a.",
            ".a",
            "a..b",
            "1abc",
            "a.1b",
            "lines[x]",
            "lines[0",
            "lines[ 0 ]",
            "lines[1234567890]",
            "a'b",
            'a"b',
            "a-b",
            "a\nb",
            "kota_é",
            "@p0",
            "",
            "order",
            "COUNT",
            "Missing",
        ]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                field(bad)

    def test_paths_the_lexer_reads_as_one_identifier_are_accepted(self) -> None:
        for good in ["a", "_x", "customer.address.city", "lines[0].sku", "lines[].sku", "m[-1]", "x.order", "count[0]"]:
            with self.subTest(good=good):
                self.assertEqual(field(good).path, good)

    def test_names_are_checked(self) -> None:
        for bad in ["orders; DROP", "a.b", "select", "1x"]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                Query(bad)
        with self.assertRaises(ValueError):
            Query("t").select(**{"as": "x"})
        with self.assertRaises(ValueError):
            Query("t").select("a; DELETE FROM t")
        with self.assertRaises(ValueError):
            Query("t").order_by("total DESC")


class ValueTests(unittest.TestCase):
    def test_unsupported_values_are_refused(self) -> None:
        for bad in [object(), {"a": 1}, b"bytes", {1, 2}]:
            with self.subTest(bad=bad), self.assertRaises(TypeError):
                field("x") == bad
        for bad in [float("nan"), float("inf"), Decimal("NaN"), 2**63]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                field("x") == bad

    def test_null_comparisons(self) -> None:
        self.assertEqual(Query("t").where(field("x") == None).to_cuteql(), "SELECT * FROM t WHERE x IS NULL")  # noqa: E711
        with self.assertRaises(ValueError):
            field("x") > None  # noqa: B015
        with self.assertRaises(ValueError):
            field("x").between(None, 1)

    def test_datetimes_are_tagged_in_utc_with_milliseconds(self) -> None:
        wib = timezone(timedelta(hours=7))
        moment = datetime(2026, 2, 1, 7, 0, 0, 123456, tzinfo=wib)
        self.assertEqual((field("t") == moment).__dict__["_value"], {"$date": "2026-02-01T00:00:00.123Z"})
        self.assertEqual((field("t") == datetime(2026, 1, 1)).__dict__["_value"], {"$date": "2026-01-01T00:00:00.000Z"})

    def test_limits_are_checked(self) -> None:
        for bad in [-1, 2**31, 1.5, True, "10"]:
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                Query("t").limit(bad)  # type: ignore[arg-type]

    def test_conditions_refuse_python_boolean_operators(self) -> None:
        with self.assertRaises(TypeError):
            (field("a") == 1) and (field("b") == 2)  # noqa: B018
        with self.assertRaises(TypeError):
            Query("t").where("total > 5")  # type: ignore[arg-type]
        with self.assertRaises(ValueError):
            and_()

    def test_same_operator_flattens_and_mixed_operators_bracket(self) -> None:
        a, b, c, d = (field(n) == 1 for n in "abcd")
        self.assertEqual(
            Query("t").where(and_(and_(a, b), or_(c, or_(d, a)))).to_cuteql(),
            "SELECT * FROM t WHERE a = @p0 AND b = @p1 AND (c = @p2 OR d = @p3 OR a = @p4)",
        )
        self.assertEqual(Query("t").where(not_(not_(a))).to_cuteql(), "SELECT * FROM t WHERE NOT (NOT (a = @p0))")


class RunTests(unittest.TestCase):
    def test_run_sends_text_and_parameters(self) -> None:
        client = CuteClient("http://cutedb.test")
        with mock.patch.object(client, "_request", return_value={"rows": []}) as request:
            client.collection("orders").query().where(field("city") == "' OR 1=1 --").limit(5).run()

        request.assert_called_once_with(
            "POST",
            "/v1/query",
            body={"query": "SELECT * FROM orders WHERE city = @p0 LIMIT 5", "parameters": {"p0": "' OR 1=1 --"}},
        )

    def test_a_built_query_refuses_extra_parameters(self) -> None:
        with self.assertRaises(ValueError):
            CuteClient().query(Query("t"), {"x": 1})

    def test_an_unbound_query_cannot_run(self) -> None:
        with self.assertRaises(ValueError):
            Query("t").run()


@unittest.skipUnless(os.environ.get("CUTEDB_TEST_SERVER"), "set CUTEDB_TEST_SERVER to run against a live server")
class LiveGoldenTests(unittest.TestCase):
    def test_every_golden_case_returns_its_rows(self) -> None:
        db = CuteClient(os.environ["CUTEDB_TEST_SERVER"], api_key=os.environ.get("CUTEDB_API_KEY"))
        db.drop_collection(C)
        db.collection(C).insert_many(GOLDEN["fixture"])

        for case in GOLDEN["cases"]:
            with self.subTest(case["name"]):
                codes = [row["code"] for row in db.query(CASES[case["name"]]())]
                if case.get("ordered"):
                    self.assertEqual(codes, case["codes"])
                else:
                    self.assertEqual(sorted(codes), sorted(case["codes"]))


if __name__ == "__main__":
    unittest.main()
