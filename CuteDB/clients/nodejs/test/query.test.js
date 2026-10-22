/**
 * Tests for the query builder.
 *
 * The golden cases are shared with the Python and Go builders through
 * `clients/querybuilder-golden.json`; the .NET suite parses and runs the same file, so agreeing
 * with it means agreeing with the real parser too.
 *
 * Set CUTEDB_TEST_SERVER (e.g. http://127.0.0.1:8420) to also run every golden case end to end
 * against a live server. That test drops and recreates the qb_golden collection.
 *
 * Run with: node --test test/
 */

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";

import { CuteClient, Query, and, escapeLike, field, not, or } from "../src/index.js";

const golden = JSON.parse(
  readFileSync(new URL("../../querybuilder-golden.json", import.meta.url), "utf8")
);
const C = golden.collection;
const utc = (y, m, d) => new Date(Date.UTC(y, m - 1, d));

/** One builder per golden case, written the way a person would write it. */
const cases = {
  select_all: () => new Query(C),
  eq_string: () => new Query(C).where(field("address.city").eq("Bandung")),
  ne_and_gt: () => new Query(C).where(field("status").ne("cancelled"), field("total").gt(200000)),
  ge_and_le: () => new Query(C).where(field("total").ge(1000)).where(field("total").le(125000.5)),
  lt_or_ge: () => new Query(C).where(field("total").lt(2000).or(field("total").ge(900000))),
  or_inside_and: () =>
    new Query(C).where(
      field("address.city").eq("Bandung"),
      or(field("status").eq("cancelled"), field("total").lt(2000))
    ),
  not: () => new Query(C).where(not(field("status").eq("paid"))),
  not_or_and_present: () =>
    new Query(C).where(
      or(field("address.city").eq("Bandung"), field("address.city").eq("Jakarta")).not(),
      field("total").isNotMissing()
    ),
  in_strings: () => new Query(C).where(field("status").in(["shipped", "draft"])),
  in_numbers: () => new Query(C).where(field("total").in([1000, 3000, 7])),
  not_in: () => new Query(C).where(field("address.city").notIn(["Bandung", "Jakarta"])),
  between: () => new Query(C).where(field("total").between(100000, 300000)),
  between_dates: () => new Query(C).where(field("placedAt").between(utc(2026, 2, 1), utc(2026, 3, 31))),
  not_between: () => new Query(C).where(field("total").notBetween(1000, 900000)),
  is_null: () => new Query(C).where(field("note").isNull()),
  ne_none_is_not_null: () => new Query(C).where(field("note").ne(null)),
  is_missing: () => new Query(C).where(field("total").isMissing()),
  is_not_missing: () => new Query(C).where(field("note").isNotMissing()),
  nested_path: () => new Query(C).where(field("customer.tier").eq("gold")),
  projecting_path: () => new Query(C).where(field("lines[].sku").eq("NR-02")),
  indexed_path: () => new Query(C).where(field("lines[0].qty").gt(3)),
  array_elementwise: () => new Query(C).where(field("tags").eq("promo")),
  array_whole: () => new Query(C).where(field("tags").eq(["promo", "bulk"])),
  bool_or_decimal: () => new Query(C).where(or(field("vip").eq(true), field("total").eq(125000.5))),
  startswith_escapes_percent: () => new Query(C).where(field("code").startsWith("100%")),
  contains_escapes_underscore: () => new Query(C).where(field("code").contains("%_")),
  endswith: () => new Query(C).where(field("customer.name").endsWith("--")),
  like_raw_pattern: () => new Query(C).where(field("code").like("O_")),
  not_like: () => new Query(C).where(field("code").notLike("1%")),
  injection_or_comment: () => new Query(C).where(field("customer.name").eq("' OR 1=1 --")),
  injection_quote: () => new Query(C).where(field("customer.name").eq("O'Brien")),
  injection_backslash: () => new Query(C).where(field("customer.name").eq('Back\\slash "quoted"')),
  injection_no_match: () => new Query(C).where(field("code").eq("x' OR '1'='1")),
  injection_startswith: () => new Query(C).where(field("customer.name").startsWith("' OR 1=1 --")),
  unicode_and_newline: () =>
    new Query(C).where(and(field("note").eq("line1\nline2"), field("customer.name").eq("Dewi Ünal 李"))),
  projection_order_limit_offset: () =>
    new Query(C)
      .select("code", { buyer: "customer.name" }, "total")
      .where(field("status").eq("paid"))
      .orderBy("total", "desc")
      .limit(2)
      .offset(1),
  order_by_two_keys: () =>
    new Query(C).where(field("address.city").eq("Bandung")).orderBy("status").orderBy("total", "desc"),
  order_limit: () => new Query(C).select("code").orderBy("code").limit(3),
  offset_only: () => new Query(C).orderBy("code").offset(7),
  select_projecting_path: () =>
    new Query(C).select("code", { skus: "lines[].sku" }).where(field("lines[].sku").eq("NR-01")),
};

/** What the server will receive: the parameters after JSON encoding. */
const asJson = (value) => JSON.parse(JSON.stringify(value));

describe("golden cases", () => {
  it("has a builder for every case", () => {
    assert.deepEqual(Object.keys(cases).sort(), golden.cases.map((c) => c.name).sort());
  });

  for (const item of golden.cases) {
    it(item.name, () => {
      const { query, parameters } = cases[item.name]().build();
      assert.equal(query, item.cuteql);
      assert.deepEqual(asJson(parameters), item.parameters);
    });
  }
});

describe("injection", () => {
  const hostile = [
    "' OR 1=1 --",
    "x'; DELETE FROM orders --",
    "''",
    "\\'",
    '" OR ""="',
    "/* comment */",
    "@p1",
    "line\nWHERE 1=1",
    "\u0000  Ünal 李 🙂",
  ];

  it("never puts a value in the text", () => {
    for (const value of hostile) {
      const { query, parameters } = new Query("orders")
        .where(
          field("name").eq(value),
          field("name").startsWith(value),
          field("tags").in([value]),
          field("name").between(value, value)
        )
        .build();
      assert.equal(
        query,
        "SELECT * FROM orders WHERE name = @p0 AND name LIKE @p1 AND tags IN @p2 AND name BETWEEN @p3 AND @p4"
      );
      assert.equal(parameters.p0, value);
      assert.deepEqual(parameters.p2, [value]);
    }
  });

  it("escapes LIKE text exactly as the LINQ provider does", () => {
    // The same three replacements as CuteExpressionTranslator.Like, backslash first.
    assert.equal(escapeLike("a\\b%c_d"), "a\\\\b\\%c\\_d");
    assert.deepEqual(new Query("t").where(field("x").startsWith("50%_\\")).parameters, { p0: "50\\%\\_\\\\%" });
  });

  it("refuses field names that are not one identifier", () => {
    for (const bad of [
      "name = 'x' OR 1=1 --", "name;", "name)", "a b", "a.", ".a", "a..b", "1abc", "a.1b",
      "lines[x]", "lines[0", "lines[ 0 ]", "lines[1234567890]", "a'b", 'a"b', "a-b", "a\nb",
      "kota_é", "@p0", "", "order", "COUNT", "Missing", 42, null, undefined,
    ]) {
      assert.throws(() => field(bad), RangeError, `accepted ${JSON.stringify(bad)}`);
    }
    for (const good of ["a", "_x", "customer.address.city", "lines[0].sku", "lines[].sku", "m[-1]", "x.order", "count[0]"]) {
      assert.equal(field(good).path, good);
    }
  });

  it("checks collection names, aliases, ordering and paging", () => {
    for (const bad of ["orders; DROP", "a.b", "select", "1x"]) {
      assert.throws(() => new Query(bad), RangeError);
    }
    assert.throws(() => new Query("t").select({ as: "x" }), RangeError);
    assert.throws(() => new Query("t").select("a; DELETE FROM t"), RangeError);
    assert.throws(() => new Query("t").orderBy("total DESC"), RangeError);
    assert.throws(() => new Query("t").orderBy("total", "DESC; --"), RangeError);
    for (const bad of [-1, 2 ** 31, 1.5, "10", NaN]) {
      assert.throws(() => new Query("t").limit(bad), RangeError);
    }
  });
});

describe("values", () => {
  it("refuses what JSON cannot carry faithfully", () => {
    for (const bad of [undefined, {}, { a: 1 }, 10n, Symbol("x"), () => 1, new Map()]) {
      assert.throws(() => field("x").eq(bad), TypeError);
    }
    for (const bad of [NaN, Infinity, new Date("nope")]) {
      assert.throws(() => field("x").eq(bad), RangeError);
    }
  });

  it("turns = null into IS NULL and refuses ordering against null", () => {
    assert.equal(new Query("t").where(field("x").eq(null)).toCuteQL(), "SELECT * FROM t WHERE x IS NULL");
    assert.throws(() => field("x").gt(null), RangeError);
    assert.throws(() => field("x").between(null, 1), RangeError);
    assert.throws(() => and(), RangeError);
    assert.throws(() => new Query("t").where("total > 5"), TypeError);
  });

  it("sends dates as UTC with milliseconds", () => {
    const q = new Query("t").where(field("t").eq(new Date("2026-02-01T07:00:00.123+07:00")));
    assert.deepEqual(q.parameters, { p0: { $date: "2026-02-01T00:00:00.123Z" } });
  });

  it("flattens the same operator and brackets a different one", () => {
    const [a, b, c, d] = ["a", "b", "c", "d"].map((n) => field(n).eq(1));
    assert.equal(
      new Query("t").where(and(and(a, b), or(c, or(d, a)))).toCuteQL(),
      "SELECT * FROM t WHERE a = @p0 AND b = @p1 AND (c = @p2 OR d = @p3 OR a = @p4)"
    );
    assert.equal(new Query("t").where(not(not(a))).toCuteQL(), "SELECT * FROM t WHERE NOT (NOT (a = @p0))");
  });
});

describe("running", () => {
  it("sends the text and the parameters", async () => {
    /** @type {any} */
    let sent;
    const db = new CuteClient("http://cutedb.test", {
      fetch: async (_url, init) => {
        sent = JSON.parse(init.body);
        return { ok: true, status: 200, text: async () => '{"kind":"select","rows":[]}' };
      },
    });

    await db.collection("orders").query().where(field("city").eq("' OR 1=1 --")).limit(5).run();
    assert.deepEqual(sent, {
      query: "SELECT * FROM orders WHERE city = @p0 LIMIT 5",
      parameters: { p0: "' OR 1=1 --" },
    });

    await assert.rejects(db.query(new Query("t"), { x: 1 }), TypeError);
    assert.throws(() => new Query("t").run(), TypeError);
  });
});

describe("live server", { skip: !process.env.CUTEDB_TEST_SERVER && "set CUTEDB_TEST_SERVER" }, () => {
  it("returns the golden rows for every case", async () => {
    const db = new CuteClient(process.env.CUTEDB_TEST_SERVER, { apiKey: process.env.CUTEDB_API_KEY });
    await db.dropCollection(C);
    await db.collection(C).insertMany(golden.fixture);

    for (const item of golden.cases) {
      const result = await db.query(cases[item.name]());
      const codes = result.rows.map((row) => row.code);
      const want = [...item.codes];
      if (!item.ordered) {
        codes.sort();
        want.sort();
      }
      assert.deepEqual(codes, want, item.name);
    }
  });
});
