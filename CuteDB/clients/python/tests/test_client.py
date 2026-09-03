"""Tests for the CuteDB Python client.

These stub the HTTP layer rather than starting a server, so they exercise the client's own
behaviour — request shape, error mapping, optional-404 handling, decimal encoding — without needing
a database or a build of the .NET server. The end-to-end check against a real server runs in CI.

Run with: python -m pytest, or python -m unittest discover tests
"""

from __future__ import annotations

import json
import sys
import unittest
import urllib.error
from decimal import Decimal
from io import BytesIO
from pathlib import Path
from typing import Any
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from cutedb import CuteClient, CuteError, CuteQueryError, QueryResult  # noqa: E402


class FakeResponse:
    """The subset of an http.client.HTTPResponse that urlopen's context manager needs."""

    def __init__(self, payload: bytes) -> None:
        self._body = BytesIO(payload)

    def read(self) -> bytes:
        return self._body.read()

    def __enter__(self) -> "FakeResponse":
        return self

    def __exit__(self, *_: object) -> None:
        return None


class ClientTestCase(unittest.TestCase):
    """Base class that captures the requests the client makes."""

    def setUp(self) -> None:
        self.client = CuteClient("http://cutedb.test")
        self.requests: list[Any] = []

    def respond(self, payload: Any, *, status: int = 200):
        """Patches urlopen to answer with one canned payload."""

        def fake_urlopen(request, timeout=None):  # noqa: ANN001, ARG001
            self.requests.append(request)

            body = payload if isinstance(payload, (bytes, str)) else json.dumps(payload)
            encoded = body.encode("utf-8") if isinstance(body, str) else body

            if status >= 400:
                raise urllib.error.HTTPError(
                    request.full_url, status, "error", {}, BytesIO(encoded)
                )

            return FakeResponse(encoded)

        return mock.patch("urllib.request.urlopen", side_effect=fake_urlopen)

    def sent_body(self, index: int = 0) -> Any:
        return json.loads(self.requests[index].data.decode("utf-8"))


class QueryTests(ClientTestCase):
    def test_sends_parameters_and_decodes_result(self) -> None:
        with self.respond(
            {
                "kind": "select",
                "columns": ["city", "revenue"],
                "rows": [{"city": "Bandung", "revenue": 7214470861.10}],
                "affected": 1,
                "durationMs": 12.5,
                "plan": "Index seek on 'orders_city'",
            }
        ):
            result = self.client.query(
                "SELECT * FROM orders WHERE address.city = @c", {"c": "Bandung"}
            )

        self.assertEqual(self.requests[0].full_url, "http://cutedb.test/v1/query")
        self.assertEqual(self.requests[0].get_method(), "POST")
        self.assertEqual(self.sent_body()["parameters"]["c"], "Bandung")

        self.assertIsInstance(result, QueryResult)
        self.assertEqual(result.rows[0]["city"], "Bandung")
        self.assertEqual(result.columns, ["city", "revenue"])
        self.assertIn("Index seek", result.plan)

    def test_omits_parameters_when_there_are_none(self) -> None:
        with self.respond({"kind": "select", "columns": [], "rows": [], "affected": 0}):
            self.client.query("SELECT * FROM orders")

        self.assertNotIn("parameters", self.sent_body())

    def test_result_is_iterable_and_sized(self) -> None:
        result = QueryResult(
            kind="select",
            columns=["n"],
            rows=[{"n": 1}, {"n": 2}],
            affected=2,
            duration_ms=1.0,
        )

        self.assertEqual(len(result), 2)
        self.assertEqual([row["n"] for row in result], [1, 2])
        self.assertEqual(result[0]["n"], 1)
        self.assertEqual(result.scalar(), 1)

    def test_scalar_on_an_empty_result_is_none(self) -> None:
        self.assertIsNone(QueryResult("select", [], [], 0, 0.0).scalar())

    def test_query_error_carries_code_and_message(self) -> None:
        with self.respond(
            {"error": "invalid_query", "message": "'~' does not belong in a query."}, status=400
        ):
            with self.assertRaises(CuteQueryError) as caught:
                self.client.query("SELECT * FROM orders WHERE total ~ 5")

        self.assertEqual(caught.exception.code, "invalid_query")
        self.assertEqual(caught.exception.status, 400)
        self.assertIn("does not belong", str(caught.exception))

    def test_non_json_error_body_still_produces_an_error(self) -> None:
        with self.respond(b"<html>gateway</html>", status=502):
            with self.assertRaises(CuteError) as caught:
                self.client.stats()

        self.assertEqual(caught.exception.status, 502)

    def test_unreachable_server_is_reported_clearly(self) -> None:
        with mock.patch(
            "urllib.request.urlopen", side_effect=urllib.error.URLError("connection refused")
        ):
            with self.assertRaises(CuteError) as caught:
                self.client.health()

        self.assertIn("Could not reach http://cutedb.test", str(caught.exception))


class CollectionTests(ClientTestCase):
    def test_insert_posts_the_document(self) -> None:
        with self.respond({"_id": "abc", "title": "halo"}):
            document = self.client.collection("notes").insert({"title": "halo"})

        self.assertEqual(document["_id"], "abc")
        self.assertEqual(self.sent_body()["title"], "halo")
        self.assertEqual(
            self.requests[0].full_url, "http://cutedb.test/v1/collections/notes/documents"
        )

    def test_decimals_are_sent_with_their_exact_digits(self) -> None:
        # json refuses Decimal outright, and going through float would turn an exact rupiah amount
        # into an approximation on the way to a database that stores it exactly.
        with self.respond({"_id": "abc"}):
            self.client.collection("orders").insert({"total": Decimal("1234567.89")})

        raw = self.requests[0].data.decode("utf-8")
        self.assertIn("1234567.89", raw)
        self.assertNotIn("1234567.8900", raw)

    def test_insert_many_sends_one_request(self) -> None:
        with self.respond({"inserted": 3, "ids": ["a", "b", "c"]}):
            ids = self.client.collection("notes").insert_many([{"n": 1}, {"n": 2}, {"n": 3}])

        self.assertEqual(ids, ["a", "b", "c"])
        self.assertEqual(len(self.requests), 1, "batching is the whole point of insert_many")
        self.assertEqual(len(self.sent_body()), 3)

    def test_insert_many_with_nothing_makes_no_request(self) -> None:
        with self.respond({}):
            self.assertEqual(self.client.collection("notes").insert_many([]), [])

        self.assertEqual(self.requests, [])

    def test_missing_document_is_not_an_error(self) -> None:
        with self.respond({"error": "not_found", "message": "no"}, status=404):
            self.assertIsNone(self.client.collection("notes").get("0" * 24))

        with self.respond({"error": "not_found", "message": "no"}, status=404):
            self.assertFalse(self.client.collection("notes").delete("0" * 24))

        with self.respond({"error": "not_found", "message": "no"}, status=404):
            self.assertFalse(self.client.collection("notes").drop_index("nope"))

    def test_other_errors_still_propagate_from_optional_calls(self) -> None:
        with self.respond({"error": "internal_error", "message": "boom"}, status=500):
            with self.assertRaises(CuteError):
                self.client.collection("notes").get("0" * 24)

    def test_find_builds_the_query_string(self) -> None:
        with self.respond({"kind": "select", "columns": [], "rows": [], "affected": 0}):
            self.client.collection("orders").find("total > 500000", limit=25, offset=50)

        url = self.requests[0].full_url
        self.assertIn("limit=25", url)
        self.assertIn("offset=50", url)
        self.assertIn("filter=total", url)

    def test_count_goes_through_an_aggregate(self) -> None:
        with self.respond({"kind": "select", "columns": ["n"], "rows": [{"n": 1234}], "affected": 1}):
            self.assertEqual(self.client.collection("orders").count("total > 100"), 1234)

        self.assertIn("SELECT COUNT(*) AS n FROM orders WHERE", self.sent_body()["query"])

    def test_create_index_returns_its_description(self) -> None:
        with self.respond(
            {"name": "city", "path": "address.city", "unique": False, "keys": 18, "entries": 50000}
        ):
            index = self.client.collection("orders").create_index("address.city")

        self.assertEqual(index.path, "address.city")
        self.assertEqual(index.keys, 18)
        self.assertEqual(self.sent_body()["unique"], False)


class TransportTests(ClientTestCase):
    def test_api_key_is_sent(self) -> None:
        client = CuteClient("http://cutedb.test", api_key="rahasia")

        def fake_urlopen(request, timeout=None):  # noqa: ANN001, ARG001
            self.requests.append(request)
            return FakeResponse(b'{"status":"ok"}')

        with mock.patch("urllib.request.urlopen", side_effect=fake_urlopen):
            client.health()

        self.assertEqual(self.requests[0].get_header("X-api-key"), "rahasia")

    def test_base_url_trailing_slash_is_trimmed(self) -> None:
        self.assertEqual(CuteClient("http://cutedb.test/").base_url, "http://cutedb.test")

    def test_works_as_a_context_manager(self) -> None:
        with CuteClient("http://cutedb.test") as client:
            self.assertIsInstance(client, CuteClient)


if __name__ == "__main__":
    unittest.main()
