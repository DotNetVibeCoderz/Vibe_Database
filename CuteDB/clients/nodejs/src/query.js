/**
 * A query builder that renders CuteQL with every value bound as a parameter.
 *
 * Concatenating a filter by hand is how injection bugs are written. This builds the statement from
 * parts instead, asks it what CuteQL it becomes, and runs it:
 *
 * @example
 * import { field } from "cutedb";
 *
 * const q = orders.query()
 *   .where(field("address.city").eq("Bandung"), field("total").gt(500_000))
 *   .select("code", "total", { buyer: "customer.name" })
 *   .orderBy("total", "desc")
 *   .limit(10);
 *
 * q.toCuteQL();   // SELECT code, total, customer.name AS buyer FROM orders
 *                 //   WHERE address.city = @p0 AND total > @p1 ORDER BY total DESC LIMIT 10
 * q.parameters;   // { p0: "Bandung", p1: 500000 }
 * await q.run();
 *
 * Two rules make it safe, and they are the whole design:
 *
 * - **Values never enter the text.** Every value becomes `@p0`, `@p1`… and travels in the request's
 *   `parameters`, which the server binds as values. There is no escaping to get wrong because
 *   nothing is escaped.
 * - **Names are checked against the lexer.** A field path, alias or collection name *is* part of
 *   the text, so it must lex as exactly one identifier. Anything else is refused rather than
 *   quoted — CuteQL has no quoted identifiers, so there is nothing safe to quote it into.
 *
 * The rendering is shared with the Python and Go builders and pinned by
 * `clients/querybuilder-golden.json`, which the .NET test suite parses and runs.
 */

// CuteLexer.Keywords. A single bare word on this list lexes as a keyword rather than an
// identifier, so `count` or `order` cannot name a field — but `customer.count` can, because a word
// containing '.' or '[' is always an identifier.
const KEYWORDS = new Set(
  `SELECT FROM WHERE ORDER BY ASC DESC LIMIT OFFSET GROUP HAVING AS AND OR NOT IN LIKE BETWEEN IS
   NULL TRUE FALSE MISSING DELETE UPDATE SET INSERT INTO VALUES DISTINCT COUNT SUM AVG MIN MAX
   EXISTS`.split(/\s+/)
);

// ASCII only, deliberately. The lexer accepts any Unicode letter, but each runtime ships its own
// Unicode tables; restricting to ASCII is what lets three languages agree on every input.
const SEGMENT = "[A-Za-z_][A-Za-z0-9_]*";
const INDEX = "\\[(?:-?[0-9]{1,9})?\\]";
const PATH = new RegExp(`^${SEGMENT}(?:${INDEX})*(?:\\.${SEGMENT}(?:${INDEX})*)*$`);
const NAME = new RegExp(`^${SEGMENT}$`);
const INT32_MAX = 2147483647;

/**
 * @param {unknown} path
 * @param {string} [what]
 * @returns {string}
 */
function checkPath(path, what = "field path") {
  if (typeof path !== "string" || !PATH.test(path)) {
    throw new RangeError(
      `${JSON.stringify(path)} is not a usable ${what}. Use letters, digits and '_', joined by '.', ` +
        "with [n] or [] after a segment — e.g. customer.address.city or lines[].sku."
    );
  }
  if (KEYWORDS.has(path.toUpperCase())) {
    throw new RangeError(`${JSON.stringify(path)} is a CuteQL keyword and cannot be used as a bare ${what}.`);
  }
  return path;
}

/**
 * @param {unknown} name
 * @param {string} what
 * @returns {string}
 */
function checkName(name, what) {
  if (typeof name !== "string" || !NAME.test(name) || KEYWORDS.has(name.toUpperCase())) {
    throw new RangeError(
      `${JSON.stringify(name)} is not a usable ${what}: it must be letters, digits and '_', and not a keyword.`
    );
  }
  return name;
}

/**
 * The parser accepts 0..int.MaxValue and nothing else; checking here gives the error a name.
 * @param {unknown} count
 * @param {string} clause
 */
function checkCount(count, clause) {
  if (!Number.isInteger(count) || /** @type {number} */ (count) < 0 || /** @type {number} */ (count) > INT32_MAX) {
    throw new RangeError(`${clause} must be an integer from 0 to ${INT32_MAX}, not ${String(count)}.`);
  }
  return /** @type {number} */ (count);
}

/**
 * Checks a value and converts it to what the server's JSON reader expects.
 * @param {unknown} value
 * @returns {unknown}
 */
function bindable(value) {
  if (value === null || typeof value === "string" || typeof value === "boolean") return value;
  if (typeof value === "number") {
    // JSON.stringify would quietly write NaN and Infinity as null — a different query, not an error.
    if (!Number.isFinite(value)) throw new RangeError(`${value} cannot be sent: JSON has no spelling for it.`);
    return value;
  }
  if (value instanceof Date) {
    if (Number.isNaN(value.getTime())) throw new RangeError("An invalid Date cannot be bound.");
    // The server reads {"$date": …} as a datetime. toISOString is always UTC with milliseconds,
    // which is the spelling the other two clients produce as well.
    return { $date: value.toISOString() };
  }
  if (Array.isArray(value)) return value.map(bindable);
  const kind = value === undefined ? "undefined" : typeof value === "object" ? value.constructor?.name ?? "object" : typeof value;
  throw new TypeError(
    `A ${kind} cannot be bound. Use a string, finite number, boolean, null, Date, or an array of those.`
  );
}

/**
 * Escapes `\`, `%` and `_` so text matches itself inside a LIKE pattern.
 *
 * The same three replacements, in the same order, as the LINQ provider's `StartsWith`: the user's
 * text is data, so a product code containing `%` must match itself rather than becoming a wildcard.
 *
 * @param {string} text
 */
export function escapeLike(text) {
  if (typeof text !== "string") throw new TypeError(`LIKE text must be a string, not ${typeof text}.`);
  return text.replaceAll("\\", "\\\\").replaceAll("%", "\\%").replaceAll("_", "\\_");
}

/** Hands out @p0, @p1… in the order the text mentions them. */
class Binder {
  constructor() {
    /** @type {Record<string, unknown>} */
    this.values = {};
    this.count = 0;
  }

  /** @param {unknown} value */
  bind(value) {
    const name = `p${this.count++}`;
    this.values[name] = value;
    return `@${name}`;
  }
}

/**
 * A predicate. Build one from a {@link Field}; combine with {@link and}, {@link or}, {@link not}.
 */
export class Condition {
  /**
   * @param {(binder: Binder) => string} render
   * @param {{ logical?: "AND" | "OR", children?: Condition[] }} [shape]
   * @internal
   */
  constructor(render, shape = {}) {
    /** @internal */
    this.render = render;
    /** @internal */
    this.logical = shape.logical;
    /** @internal */
    this.children = shape.children;
    Object.freeze(this);
  }

  /** @param {Condition[]} others */
  and(...others) {
    return and(this, ...others);
  }

  /** @param {Condition[]} others */
  or(...others) {
    return or(this, ...others);
  }

  not() {
    return not(this);
  }
}

/**
 * @param {"AND" | "OR"} operator
 * @param {Condition[]} conditions
 */
function logical(operator, conditions) {
  /** @type {Condition[]} */
  const children = [];
  for (const condition of conditions) {
    if (!(condition instanceof Condition)) {
      throw new TypeError(`${operator} takes conditions, not ${typeof condition}.`);
    }
    // (a AND b) AND c is a AND b AND c; flattening keeps the brackets for where they matter.
    if (condition.logical === operator) children.push(...(condition.children ?? []));
    else children.push(condition);
  }
  if (children.length === 0) throw new RangeError(`${operator} needs at least one condition.`);
  if (children.length === 1) return children[0];

  return new Condition(
    (binder) =>
      children
        // A nested AND/OR is always bracketed. Precedence would make some of those brackets
        // redundant, but always writing them is a rule all three builders can follow identically.
        .map((child) => (child.logical ? `(${child.render(binder)})` : child.render(binder)))
        .join(` ${operator} `),
    { logical: operator, children }
  );
}

/**
 * All of the conditions.
 * @param {Condition[]} conditions
 */
export function and(...conditions) {
  return logical("AND", conditions);
}

/**
 * Any of the conditions.
 * @param {Condition[]} conditions
 */
export function or(...conditions) {
  return logical("OR", conditions);
}

/**
 * The negation of a condition. Remember MISSING: `NOT (x > 0)` still skips rows without x.
 * @param {Condition} condition
 */
export function not(condition) {
  if (!(condition instanceof Condition)) throw new TypeError(`not takes a condition, not ${typeof condition}.`);
  return new Condition((binder) => `NOT (${condition.render(binder)})`);
}

/**
 * A field path to build conditions on — `total`, `customer.address.city`, `lines[0].sku`,
 * `lines[].sku`.
 *
 * A field holding an array compares element-wise: `field("tags").eq("promo")` means "tags contains
 * promo".
 */
export class Field {
  /** @param {string} path */
  constructor(path) {
    /** @type {string} */
    this.path = checkPath(path);
    Object.freeze(this);
  }

  /**
   * @param {string} operator
   * @param {unknown} value already checked
   */
  #binary(operator, value) {
    const path = this.path;
    return new Condition((binder) => `${path} ${operator} ${binder.bind(value)}`);
  }

  /** @param {string} suffix */
  #is(suffix) {
    const path = this.path;
    return new Condition(() => `${path} IS ${suffix}`);
  }

  /**
   * @param {string} operator
   * @param {unknown} value
   */
  #ordered(operator, value) {
    if (value === null || value === undefined) {
      throw new RangeError(`'${this.path} ${operator} NULL' is never true; use isNull() instead.`);
    }
    return this.#binary(operator, bindable(value));
  }

  /**
   * `= value`. Against null this is `IS NULL`, because `= NULL` is never true.
   * @param {unknown} value
   */
  eq(value) {
    return value === null ? this.#is("NULL") : this.#binary("=", bindable(value));
  }

  /**
   * `!= value`. Against null this is `IS NOT NULL`.
   * @param {unknown} value
   */
  ne(value) {
    return value === null ? this.#is("NOT NULL") : this.#binary("!=", bindable(value));
  }

  /** @param {unknown} value */
  lt(value) {
    return this.#ordered("<", value);
  }

  /** @param {unknown} value */
  le(value) {
    return this.#ordered("<=", value);
  }

  /** @param {unknown} value */
  gt(value) {
    return this.#ordered(">", value);
  }

  /** @param {unknown} value */
  ge(value) {
    return this.#ordered(">=", value);
  }

  /**
   * `IN` the given values, sent as one array parameter.
   * @param {unknown[]} values
   */
  in(values) {
    return this.#binary("IN", Field.#list(values));
  }

  /** @param {unknown[]} values */
  notIn(values) {
    return this.#binary("NOT IN", Field.#list(values));
  }

  /**
   * Inclusive at both ends.
   * @param {unknown} low
   * @param {unknown} high
   */
  between(low, high) {
    return this.#between(low, high, false);
  }

  /**
   * @param {unknown} low
   * @param {unknown} high
   */
  notBetween(low, high) {
    return this.#between(low, high, true);
  }

  /**
   * A raw LIKE pattern: `%` and `_` are wildcards. Use the helpers for user text.
   * @param {string} pattern
   */
  like(pattern) {
    return this.#binary("LIKE", Field.#text(pattern));
  }

  /** @param {string} pattern */
  notLike(pattern) {
    return this.#binary("NOT LIKE", Field.#text(pattern));
  }

  /**
   * Begins with `text` taken literally — its `%` and `_` are escaped.
   * @param {string} text
   */
  startsWith(text) {
    return this.#binary("LIKE", `${escapeLike(text)}%`);
  }

  /** @param {string} text */
  endsWith(text) {
    return this.#binary("LIKE", `%${escapeLike(text)}`);
  }

  /**
   * Has `text` as a substring. For "array contains value", use `eq`.
   * @param {string} text
   */
  contains(text) {
    return this.#binary("LIKE", `%${escapeLike(text)}%`);
  }

  /** Absent or explicitly null. */
  isNull() {
    return this.#is("NULL");
  }

  isNotNull() {
    return this.#is("NOT NULL");
  }

  /** Absent — not written at all. Distinct from null. */
  isMissing() {
    return this.#is("MISSING");
  }

  isNotMissing() {
    return this.#is("NOT MISSING");
  }

  /**
   * @param {unknown} low
   * @param {unknown} high
   * @param {boolean} negated
   */
  #between(low, high, negated) {
    if (low == null || high == null) throw new RangeError(`BETWEEN on '${this.path}' needs two non-null bounds.`);
    const path = this.path;
    const [lo, hi] = [bindable(low), bindable(high)];
    const keyword = negated ? "NOT BETWEEN" : "BETWEEN";
    return new Condition((binder) => `${path} ${keyword} ${binder.bind(lo)} AND ${binder.bind(hi)}`);
  }

  /** @param {unknown} values */
  static #list(values) {
    if (!Array.isArray(values)) throw new TypeError("in() takes an array of values.");
    return values.map(bindable);
  }

  /** @param {unknown} pattern */
  static #text(pattern) {
    if (typeof pattern !== "string") throw new TypeError(`LIKE takes a string pattern, not ${typeof pattern}.`);
    return pattern;
  }
}

/**
 * A field path to build conditions on: `field("address.city").eq("Bandung")`.
 * @param {string} path
 */
export function field(path) {
  return new Field(path);
}

/**
 * A SELECT over one collection.
 *
 * Each method changes the query and returns it, so calls chain. Create one with
 * `collection.query()` to be able to {@link Query#run} it, or `new Query("orders")` to only render
 * it — or hand it to `db.query(q)`.
 */
export class Query {
  /** @type {string[]} */
  #projections = [];
  /** @type {Condition[]} */
  #where = [];
  /** @type {string[]} */
  #order = [];
  /** @type {number | undefined} */
  #limit = undefined;
  #offset = 0;

  /**
   * @param {string} collection
   * @param {{ query: (q: Query) => Promise<any>, explain: (text: string, parameters?: Record<string, unknown>) => Promise<any> }} [client]
   */
  constructor(collection, client) {
    /** @type {string} */
    this.collection = checkName(collection, "collection name");
    /** @internal */
    this.client = client;
  }

  /**
   * Project fields. Strings are paths; an object maps aliases to paths:
   * `select("code", { buyer: "customer.name" })` → `code, customer.name AS buyer`.
   * @param {(string | Record<string, string>)[]} items
   */
  select(...items) {
    for (const item of items) {
      if (typeof item === "string") {
        this.#projections.push(checkPath(item));
      } else if (item && typeof item === "object" && !Array.isArray(item)) {
        for (const [alias, path] of Object.entries(item)) {
          this.#projections.push(`${checkPath(path)} AS ${checkName(alias, "alias")}`);
        }
      } else {
        throw new TypeError("select() takes paths, or an object of { alias: path }.");
      }
    }
    return this;
  }

  /**
   * Filter. Several conditions, or several calls, are combined with AND.
   * @param {Condition[]} conditions
   */
  where(...conditions) {
    for (const condition of conditions) {
      if (!(condition instanceof Condition)) {
        throw new TypeError(`where() takes conditions such as field("total").gt(5), not ${typeof condition}.`);
      }
      this.#where.push(condition);
    }
    return this;
  }

  /**
   * Sort. Call again for a second key.
   * @param {string} path
   * @param {"asc" | "desc"} [direction]
   */
  orderBy(path, direction = "asc") {
    if (direction !== "asc" && direction !== "desc") {
      throw new RangeError(`Sort direction must be "asc" or "desc", not ${JSON.stringify(direction)}.`);
    }
    this.#order.push(checkPath(path) + (direction === "desc" ? " DESC" : ""));
    return this;
  }

  /** @param {number} count */
  limit(count) {
    this.#limit = checkCount(count, "LIMIT");
    return this;
  }

  /** @param {number} count */
  offset(count) {
    this.#offset = checkCount(count, "OFFSET");
    return this;
  }

  /**
   * The CuteQL text and the parameters it refers to, as `/v1/query` takes them.
   * @returns {{ query: string, parameters: Record<string, unknown> }}
   */
  build() {
    const binder = new Binder();
    let text = `SELECT ${this.#projections.length ? this.#projections.join(", ") : "*"} FROM ${this.collection}`;
    if (this.#where.length) text += ` WHERE ${and(...this.#where).render(binder)}`;
    if (this.#order.length) text += ` ORDER BY ${this.#order.join(", ")}`;
    if (this.#limit !== undefined) text += ` LIMIT ${this.#limit}`;
    if (this.#offset) text += ` OFFSET ${this.#offset}`;
    return { query: text, parameters: binder.values };
  }

  /** The statement this query becomes, with `@p0`-style placeholders for its values. */
  toCuteQL() {
    return this.build().query;
  }

  /** The values bound to the placeholders, as they will be sent. */
  get parameters() {
    return this.build().parameters;
  }

  toString() {
    return this.toCuteQL();
  }

  /** Runs the query on the server it was created from. */
  run() {
    return this.#client().query(this);
  }

  /** How the server would find the rows, without returning them. */
  explain() {
    const { query, parameters } = this.build();
    return this.#client().explain(query, parameters);
  }

  #client() {
    if (!this.client) {
      throw new TypeError("This query has no client. Create it with collection.query(), or pass it to db.query().");
    }
    return this.client;
  }
}
