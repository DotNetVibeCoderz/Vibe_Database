/**
 * CuteDB client for Node.js.
 *
 * Built by Gravicode Studios, led by Kang Fadhil.
 * https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB
 *
 * CuteDB is an embedded database, so this package talks to `cutedb-server` over HTTP rather than
 * binding to the engine. One HTTP endpoint is a far smaller surface to keep correct across three
 * languages and six platforms than three sets of native bindings would be, and the network hop is
 * irrelevant next to the work most calls do.
 *
 * Written as plain ESM with JSDoc types and a hand-written `index.d.ts`, so there is no build step
 * between the source you read and the code that runs. Requires Node 18 or newer for global fetch.
 *
 * @example
 * import { CuteClient } from "cutedb";
 *
 * const db = new CuteClient("http://127.0.0.1:8420");
 * const orders = db.collection("orders");
 *
 * await orders.insert({ customer: "Sari", total: 249000 });
 *
 * const result = await db.query(
 *   "SELECT address.city AS city, SUM(total) AS revenue FROM orders GROUP BY address.city"
 * );
 * for (const row of result.rows) console.log(row.city, row.revenue);
 */

/** This client's version. */
export const VERSION = "2.0.1";

/**
 * Anything the server refused, or any transport failure.
 */
export class CuteError extends Error {
  /**
   * @param {string} message
   * @param {{ code?: string, status?: number, cause?: unknown }} [options]
   */
  constructor(message, options = {}) {
    super(message, options.cause === undefined ? undefined : { cause: options.cause });
    this.name = "CuteError";
    /** Machine-readable code, such as `invalid_query`. */
    this.code = options.code;
    /** HTTP status code, when the failure came from the server. */
    this.status = options.status;
  }

  /** True when the server answered 404 — a missing document, collection or index. */
  get isNotFound() {
    return this.status === 404;
  }

  /** True when the failure was CuteQL that would not parse or would not run. */
  get isQueryError() {
    return this.code === "invalid_query";
  }
}

/**
 * The one place an HTTP request is made.
 *
 * A module-level function rather than a method, so that `CuteCollection` can reach it without
 * either class exposing a transport on its public surface.
 *
 * @param {CuteClient} client
 * @param {string} method
 * @param {string} path
 * @param {{ body?: unknown, query?: Record<string, unknown> }} [options]
 * @returns {Promise<any>}
 */
async function request(client, method, path, options = {}) {
  let url = client.baseUrl + path;

  if (options.query) {
    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(options.query)) {
      if (value !== undefined && value !== null) search.set(key, String(value));
    }
    const encoded = search.toString();
    if (encoded) url += `?${encoded}`;
  }

  /** @type {Record<string, string>} */
  const headers = { Accept: "application/json" };
  if (client.apiKey) headers["X-API-Key"] = client.apiKey;

  let payload;
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json; charset=utf-8";
    payload = JSON.stringify(options.body);
  }

  // AbortSignal.timeout rather than a manual setTimeout: it cleans itself up, and it does not keep
  // the event loop alive after the request settles.
  const signal =
    typeof AbortSignal !== "undefined" && typeof AbortSignal.timeout === "function"
      ? AbortSignal.timeout(client.timeoutMs)
      : undefined;

  let response;
  try {
    response = await client.fetch(url, { method, headers, body: payload, signal });
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error);
    throw new CuteError(`Could not reach ${client.baseUrl}: ${reason}`, { cause: error });
  }

  const text = await response.text();

  if (!response.ok) {
    throw toError(response.status, text);
  }

  if (!text) return undefined;

  try {
    return JSON.parse(text);
  } catch (error) {
    throw new CuteError("The server returned a body that is not JSON.", { cause: error });
  }
}

/**
 * Runs a request that answers 404 for "there was nothing there", turning that into `false` rather
 * than an exception.
 *
 * @param {CuteClient} client
 * @param {string} method
 * @param {string} path
 * @returns {Promise<boolean>}
 */
async function requestOptional(client, method, path) {
  try {
    await request(client, method, path);
    return true;
  } catch (error) {
    if (error instanceof CuteError && error.isNotFound) return false;
    throw error;
  }
}

/**
 * @param {number} status
 * @param {string} body
 */
function toError(status, body) {
  let code;
  let message = `Request failed with status ${status}.`;

  // The server always sends JSON for its own failures. A body that is not JSON means something
  // else answered — a proxy, usually — and the status is all there is to go on.
  try {
    const payload = JSON.parse(body);
    code = payload.error;
    if (payload.message) message = payload.message;
  } catch {
    /* not JSON; keep the generic message */
  }

  return new CuteError(message, { code, status });
}

/**
 * A connection to a CuteDB server.
 *
 * Stateless — there is no session to keep alive — so one instance is fine to share across a whole
 * application.
 */
export class CuteClient {
  /**
   * @param {string} [baseUrl] Where the server is listening.
   * @param {{ apiKey?: string, timeoutMs?: number, fetch?: typeof globalThis.fetch }} [options]
   */
  constructor(baseUrl = "http://127.0.0.1:8420", options = {}) {
    /** @type {string} */
    this.baseUrl = baseUrl.replace(/\/+$/, "");
    /** @type {string | undefined} */
    this.apiKey = options.apiKey;
    /** @type {number} */
    this.timeoutMs = options.timeoutMs ?? 30_000;

    // Injectable so tests can stub it and so a caller behind a proxy can supply their own.
    /** @type {typeof globalThis.fetch} */
    this.fetch = options.fetch ?? globalThis.fetch;

    if (typeof this.fetch !== "function") {
      throw new CuteError(
        "No fetch implementation. Node 18 or newer provides one; on older versions pass { fetch }."
      );
    }
  }

  /** Checks the server is up, and which engine build it is running. */
  health() {
    return request(this, "GET", "/health");
  }

  /**
   * A handle to a collection. Created on first insert if it does not exist.
   * @param {string} name
   */
  collection(name) {
    return new CuteCollection(this, name);
  }

  /** Every collection, with its size. */
  collections() {
    return request(this, "GET", "/v1/collections");
  }

  /**
   * Drops a collection and everything in it. Resolves false when there was none.
   * @param {string} name
   */
  dropCollection(name) {
    return requestOptional(this, "DELETE", `/v1/collections/${encodeURIComponent(name)}`);
  }

  /**
   * Runs a CuteQL statement.
   *
   * Bind values through `parameters` rather than building the statement by concatenation: a bound
   * value is used as a value and can never be reinterpreted as syntax.
   *
   * @param {string} query
   * @param {Record<string, unknown>} [parameters]
   */
  query(query, parameters) {
    return request(this, "POST", "/v1/query", {
      body: parameters ? { query, parameters } : { query },
    });
  }

  /**
   * Reports how a SELECT would find its rows, without returning them.
   * @param {string} query
   * @param {Record<string, unknown>} [parameters]
   */
  explain(query, parameters) {
    return request(this, "POST", "/v1/explain", {
      body: parameters ? { query, parameters } : { query },
    });
  }

  /** Totals across the database. */
  stats() {
    return request(this, "GET", "/v1/stats");
  }

  /** Rewrites the file with only current state. Resolves the bytes reclaimed. */
  async compact() {
    const result = await request(this, "POST", "/v1/compact");
    return result?.reclaimedBytes ?? 0;
  }
}

/**
 * A handle to one collection.
 */
export class CuteCollection {
  /**
   * @param {CuteClient} client
   * @param {string} name
   */
  constructor(client, name) {
    /** @type {CuteClient} */
    this.client = client;
    /** @type {string} */
    this.name = name;
    /** @type {string} */
    this.base = `/v1/collections/${encodeURIComponent(name)}`;
  }

  /**
   * Stores one document and resolves it with the id the server assigned.
   * @param {Record<string, unknown>} document
   */
  insert(document) {
    return request(this.client, "POST", `${this.base}/documents`, { body: document });
  }

  /**
   * Stores many documents in one request and resolves their ids.
   *
   * Much faster than a loop over {@link insert}: the server applies the whole batch under a single
   * lock and flushes once, rather than once per document.
   *
   * @param {Record<string, unknown>[]} documents
   * @returns {Promise<string[]>}
   */
  async insertMany(documents) {
    if (!documents.length) return [];
    const result = await request(this.client, "POST", `${this.base}/documents`, {
      body: documents,
    });
    return result?.ids ?? [];
  }

  /**
   * Fetches a document by id, or null.
   * @param {string} id
   */
  async get(id) {
    try {
      return await request(this.client, "GET", `${this.base}/documents/${encodeURIComponent(id)}`);
    } catch (error) {
      if (error instanceof CuteError && error.isNotFound) return null;
      throw error;
    }
  }

  /**
   * Replaces a document wholesale. The id in the URL wins over any `_id` in the body.
   * @param {string} id
   * @param {Record<string, unknown>} document
   */
  replace(id, document) {
    return request(this.client, "PUT", `${this.base}/documents/${encodeURIComponent(id)}`, {
      body: document,
    });
  }

  /**
   * Merges fields into a document.
   *
   * A key containing a dot is treated as a path, so `{"address.city": "Bandung"}` reaches into the
   * subdocument while `{"address": {…}}` replaces it.
   *
   * @param {string} id
   * @param {Record<string, unknown>} changes
   */
  patch(id, changes) {
    return request(this.client, "PATCH", `${this.base}/documents/${encodeURIComponent(id)}`, {
      body: changes,
    });
  }

  /**
   * Deletes a document. Resolves false when it was not there.
   * @param {string} id
   */
  delete(id) {
    return requestOptional(
      this.client,
      "DELETE",
      `${this.base}/documents/${encodeURIComponent(id)}`
    );
  }

  /**
   * Pages through the collection, optionally filtered by a CuteQL WHERE expression.
   * @param {{ filter?: string, limit?: number, offset?: number }} [options]
   */
  find(options = {}) {
    return request(this.client, "GET", `${this.base}/documents`, {
      query: { filter: options.filter, limit: options.limit, offset: options.offset },
    });
  }

  /**
   * How many documents match a filter, or how many there are in total.
   * @param {string} [filter]
   */
  async count(filter) {
    const where = filter ? ` WHERE ${filter}` : "";
    const result = await this.client.query(`SELECT COUNT(*) AS n FROM ${this.name}${where}`);
    return result.rows[0]?.n ?? 0;
  }

  /** This collection's size and indexes. */
  stats() {
    return request(this.client, "GET", this.base);
  }

  /**
   * Creates a secondary index over a document path.
   * @param {string} path
   * @param {{ name?: string, unique?: boolean }} [options]
   */
  createIndex(path, options = {}) {
    return request(this.client, "POST", `${this.base}/indexes`, {
      body: { path, name: options.name, unique: options.unique ?? false },
    });
  }

  /**
   * Drops an index. Resolves false when there was none by that name.
   * @param {string} name
   */
  dropIndex(name) {
    return requestOptional(
      this.client,
      "DELETE",
      `${this.base}/indexes/${encodeURIComponent(name)}`
    );
  }
}

export default CuteClient;
