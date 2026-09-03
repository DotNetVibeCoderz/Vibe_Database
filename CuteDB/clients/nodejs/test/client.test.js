/**
 * Tests for the CuteDB Node client.
 *
 * These stub `fetch` rather than starting a server, so they exercise the client's own behaviour —
 * request shape, error mapping, optional-404 handling — without needing a database or a build of
 * the .NET server. The end-to-end check against a real server runs in CI.
 *
 * Run with: node --test test/
 */

import assert from "node:assert/strict";
import { describe, it } from "node:test";

import { CuteClient, CuteError, VERSION } from "../src/index.js";

/**
 * Builds a client whose fetch is a stub, and records what it was called with.
 *
 * @param {(url: string, init: RequestInit) => { status?: number, body?: unknown, text?: string }} handler
 */
function stubbed(handler) {
  /** @type {{ url: string, init: any }[]} */
  const calls = [];

  const client = new CuteClient("http://cutedb.test", {
    fetch: async (url, init) => {
      calls.push({ url: String(url), init });
      const response = handler(String(url), init ?? {});
      const status = response.status ?? 200;
      const text = response.text ?? JSON.stringify(response.body ?? {});

      return {
        ok: status >= 200 && status < 300,
        status,
        text: async () => text,
      };
    },
  });

  return { client, calls };
}

describe("CuteClient", () => {
  it("exports a version", () => {
    assert.equal(typeof VERSION, "string");
  });

  it("trims a trailing slash from the base URL", () => {
    const { client } = stubbed(() => ({ body: {} }));
    assert.equal(client.baseUrl, "http://cutedb.test");

    const trailing = new CuteClient("http://cutedb.test///", { fetch: async () => ({}) });
    assert.equal(trailing.baseUrl, "http://cutedb.test");
  });

  it("sends a query with its parameters and decodes the result", async () => {
    const { client, calls } = stubbed(() => ({
      body: {
        kind: "select",
        columns: ["city", "revenue"],
        rows: [{ city: "Bandung", revenue: 7214470861.1 }],
        affected: 1,
        durationMs: 12.5,
        plan: "Index seek on 'orders_city'",
      },
    }));

    const result = await client.query("SELECT * FROM orders WHERE address.city = @c", {
      c: "Bandung",
    });

    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, "http://cutedb.test/v1/query");
    assert.equal(calls[0].init.method, "POST");

    const sent = JSON.parse(calls[0].init.body);
    assert.equal(sent.parameters.c, "Bandung");

    assert.equal(result.rows[0].city, "Bandung");
    assert.deepEqual(result.columns, ["city", "revenue"]);
    assert.match(result.plan, /Index seek/);
  });

  it("omits the parameters key when there are none", async () => {
    const { client, calls } = stubbed(() => ({ body: { kind: "select", rows: [], columns: [] } }));
    await client.query("SELECT * FROM orders");

    const sent = JSON.parse(calls[0].init.body);
    assert.equal("parameters" in sent, false);
  });

  it("turns a server error into a CuteError carrying its code", async () => {
    const { client } = stubbed(() => ({
      status: 400,
      body: { error: "invalid_query", message: "'~' does not belong in a query." },
    }));

    await assert.rejects(
      () => client.query("SELECT * FROM orders WHERE total ~ 5"),
      (error) => {
        assert.ok(error instanceof CuteError);
        assert.equal(error.isQueryError, true);
        assert.match(error.message, /does not belong/);
        return true;
      }
    );
  });

  it("falls back to a generic message when the body is not JSON", async () => {
    const { client } = stubbed(() => ({ status: 502, text: "<html>gateway</html>" }));

    await assert.rejects(
      () => client.stats(),
      (error) => {
        assert.ok(error instanceof CuteError);
        assert.equal(error.status, 502);
        assert.match(error.message, /502/);
        return true;
      }
    );
  });

  it("reports an unreachable server rather than leaking the transport error", async () => {
    const client = new CuteClient("http://cutedb.test", {
      fetch: async () => {
        throw new TypeError("connect ECONNREFUSED");
      },
    });

    await assert.rejects(
      () => client.health(),
      (error) => {
        assert.ok(error instanceof CuteError);
        assert.match(error.message, /Could not reach http:\/\/cutedb\.test/);
        return true;
      }
    );
  });

  it("sends the API key when it has one", async () => {
    const { calls } = (() => {
      const stub = stubbed(() => ({ body: { status: "ok" } }));
      stub.client.apiKey = "rahasia";
      stub.client.health();
      return stub;
    })();

    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(calls[0].init.headers["X-API-Key"], "rahasia");
  });

  it("refuses to construct when fetch is not callable", () => {
    // Passing null falls back to the global fetch, which exists on Node 18+. The guard is there
    // for older runtimes and for a caller who hands over something that is not a function.
    assert.throws(
      () => new CuteClient("http://cutedb.test", { fetch: /** @type {any} */ ("nope") }),
      CuteError
    );
  });
});

describe("CuteCollection", () => {
  it("percent-encodes the collection name into the path", () => {
    const { client } = stubbed(() => ({ body: {} }));
    assert.equal(client.collection("my orders").base, "/v1/collections/my%20orders");
  });

  it("returns null rather than throwing for a missing document", async () => {
    const { client } = stubbed(() => ({ status: 404, body: { error: "not_found", message: "no" } }));

    assert.equal(await client.collection("orders").get("0".repeat(24)), null);
    assert.equal(await client.collection("orders").delete("0".repeat(24)), false);
    assert.equal(await client.collection("orders").dropIndex("nope"), false);
  });

  it("sends one request for a batch insert", async () => {
    const { client, calls } = stubbed(() => ({ body: { inserted: 3, ids: ["a", "b", "c"] } }));

    const ids = await client.collection("notes").insertMany([{ n: 1 }, { n: 2 }, { n: 3 }]);

    assert.equal(calls.length, 1, "batching is the whole point of insertMany");
    assert.deepEqual(ids, ["a", "b", "c"]);
    assert.equal(JSON.parse(calls[0].init.body).length, 3);
  });

  it("makes no request for an empty batch", async () => {
    const { client, calls } = stubbed(() => ({ body: {} }));

    assert.deepEqual(await client.collection("notes").insertMany([]), []);
    assert.equal(calls.length, 0);
  });

  it("builds the find query string from its options", async () => {
    const { client, calls } = stubbed(() => ({ body: { kind: "select", rows: [], columns: [] } }));

    await client.collection("orders").find({ filter: "total > 500000", limit: 25, offset: 50 });

    const url = new URL(calls[0].url);
    assert.equal(url.searchParams.get("filter"), "total > 500000");
    assert.equal(url.searchParams.get("limit"), "25");
    assert.equal(url.searchParams.get("offset"), "50");
  });

  it("leaves absent find options out of the query string", async () => {
    const { client, calls } = stubbed(() => ({ body: { kind: "select", rows: [], columns: [] } }));

    await client.collection("orders").find();

    assert.equal(new URL(calls[0].url).search, "", "no options should mean no query string");
  });

  it("counts through a CuteQL aggregate", async () => {
    const { client, calls } = stubbed(() => ({
      body: { kind: "select", columns: ["n"], rows: [{ n: 1234 }], affected: 1 },
    }));

    assert.equal(await client.collection("orders").count("total > 100"), 1234);
    assert.match(JSON.parse(calls[0].init.body).query, /SELECT COUNT\(\*\) AS n FROM orders WHERE/);
  });
});
