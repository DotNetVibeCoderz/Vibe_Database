/**
 * Type declarations for the CuteDB Node.js client.
 *
 * Hand-written rather than generated, so the package has no build step: what you import is the
 * source you can read.
 */

/** This client's version. */
export declare const VERSION: string;

/** One CuteDB document. The reserved `_id` field holds its identifier. */
export interface Document {
  _id?: string;
  [field: string]: unknown;
}

/** What a statement produced. */
export interface QueryResult {
  kind: "select" | "insert" | "update" | "delete";
  /**
   * Field names present across the returned rows, in first-seen order. A collection has no schema,
   * so these are discovered from the data rather than declared.
   */
  columns: string[];
  rows: Document[];
  /** Documents inserted, updated or deleted; for a SELECT, the row count. */
  affected: number;
  /** How long the statement took on the server. */
  durationMs: number;
  /** How the rows were found. */
  plan: string;
}

/** One secondary index. */
export interface IndexInfo {
  name: string;
  path: string;
  unique: boolean;
  keys: number;
  entries: number;
}

/** Totals across a database. */
export interface Stats {
  path: string;
  collections: number;
  documents: number;
  fileBytes: number;
  liveBytes: number;
  deadBytes: number;
  reservedBytes: number;
  /** File size divided by live data. Much above 2 means most of the file is history. */
  fileAmplification: number;
  createdAt: string;
  engine: string;
}

/** One collection's size and indexes. */
export interface CollectionStats {
  name: string;
  documents: number;
  liveBytes: number;
  deadBytes: number;
  reservedBytes: number;
  averageDocumentBytes: number;
  indexes: IndexInfo[];
}

/** How a query would be executed. */
export interface QueryPlan {
  strategy: string;
  index: string | null;
  candidateRows: number;
  matchedRows: number;
  nativeScanner: boolean;
  description: string;
}

/** Anything the server refused, or any transport failure. */
export declare class CuteError extends Error {
  constructor(message: string, options?: { code?: string; status?: number; cause?: unknown });
  /** Machine-readable code, such as `invalid_query`. */
  readonly code?: string;
  /** HTTP status code, when the failure came from the server. */
  readonly status?: number;
  /** True when the server answered 404. */
  readonly isNotFound: boolean;
  /** True when the failure was CuteQL that would not parse or would not run. */
  readonly isQueryError: boolean;
}

export interface CuteClientOptions {
  /** Sent as `X-API-Key` with every request. */
  apiKey?: string;
  /** Per-request timeout. Default 30000. */
  timeoutMs?: number;
  /** Supply your own fetch, for a proxy agent or for tests. */
  fetch?: typeof globalThis.fetch;
}

export interface FindOptions {
  /** A CuteQL WHERE expression, such as `address.city = 'Bandung' AND total > 500000`. */
  filter?: string;
  /** Rows to return. Default 100, maximum 10000. */
  limit?: number;
  /** Rows to skip, for paging. */
  offset?: number;
}

/** A handle to one collection. */
export declare class CuteCollection {
  constructor(client: CuteClient, name: string);
  readonly client: CuteClient;
  readonly name: string;

  insert(document: Document): Promise<Document>;
  insertMany(documents: Document[]): Promise<string[]>;
  get(id: string): Promise<Document | null>;
  replace(id: string, document: Document): Promise<Document>;
  patch(id: string, changes: Document): Promise<Document>;
  delete(id: string): Promise<boolean>;
  find(options?: FindOptions): Promise<QueryResult>;
  count(filter?: string): Promise<number>;
  /** Starts a query builder over this collection that can be run with `.run()`. */
  query(): Query;
  stats(): Promise<CollectionStats>;
  createIndex(path: string, options?: { name?: string; unique?: boolean }): Promise<IndexInfo>;
  dropIndex(name: string): Promise<boolean>;
}

/** A connection to a CuteDB server. */
export declare class CuteClient {
  constructor(baseUrl?: string, options?: CuteClientOptions);
  readonly baseUrl: string;
  readonly apiKey?: string;
  readonly timeoutMs: number;

  health(): Promise<{ status: string; engine: string }>;
  collection(name: string): CuteCollection;
  collections(): Promise<CollectionStats[]>;
  dropCollection(name: string): Promise<boolean>;
  /** Runs CuteQL text with parameters, or a built {@link Query} (which carries its own). */
  query(query: string, parameters?: Record<string, unknown>): Promise<QueryResult>;
  query(query: Query): Promise<QueryResult>;
  explain(query: string, parameters?: Record<string, unknown>): Promise<QueryPlan>;
  stats(): Promise<Stats>;
  compact(): Promise<number>;
}

// --- query builder ------------------------------------------------------------------------------
//
// Values are never written into the text: each becomes @p0, @p1… and is sent as a parameter.
// Field paths, aliases and collection names must lex as a single CuteQL identifier — ASCII
// letters, digits and '_', joined by '.', with [n] or [] after a segment — and not be a bare
// keyword; anything else throws a RangeError.

/** What a builder can bind: anything else throws a TypeError. Dates are sent as `{"$date": …}`. */
export type BindableValue = string | number | boolean | null | Date | BindableValue[];

/** A predicate. Build one from a {@link Field}; combine with {@link and}, {@link or}, {@link not}. */
export declare class Condition {
  private constructor();
  and(...others: Condition[]): Condition;
  or(...others: Condition[]): Condition;
  not(): Condition;
}

/**
 * A field path — `total`, `customer.address.city`, `lines[0].sku`, `lines[].sku`. A field holding
 * an array compares element-wise: `field("tags").eq("promo")` means "tags contains promo".
 */
export declare class Field {
  constructor(path: string);
  readonly path: string;
  /** `= value`; against `null`, `IS NULL`. */
  eq(value: BindableValue): Condition;
  /** `!= value`; against `null`, `IS NOT NULL`. */
  ne(value: BindableValue): Condition;
  lt(value: Exclude<BindableValue, null>): Condition;
  le(value: Exclude<BindableValue, null>): Condition;
  gt(value: Exclude<BindableValue, null>): Condition;
  ge(value: Exclude<BindableValue, null>): Condition;
  /** `IN` the values, sent as one array parameter. */
  in(values: BindableValue[]): Condition;
  notIn(values: BindableValue[]): Condition;
  /** Inclusive at both ends. */
  between(low: Exclude<BindableValue, null>, high: Exclude<BindableValue, null>): Condition;
  notBetween(low: Exclude<BindableValue, null>, high: Exclude<BindableValue, null>): Condition;
  /** A raw pattern: `%` and `_` are wildcards. Use the helpers below for user text. */
  like(pattern: string): Condition;
  notLike(pattern: string): Condition;
  /** Text taken literally; its `%` and `_` are escaped. */
  startsWith(text: string): Condition;
  endsWith(text: string): Condition;
  contains(text: string): Condition;
  isNull(): Condition;
  isNotNull(): Condition;
  isMissing(): Condition;
  isNotMissing(): Condition;
}

/** A SELECT over one collection. Each method changes the query and returns it. */
export declare class Query {
  constructor(collection: string);
  readonly collection: string;
  /** Strings are paths; an object maps aliases to paths: `select("code", { buyer: "customer.name" })`. */
  select(...items: (string | Record<string, string>)[]): this;
  /** Several conditions, or several calls, are combined with AND. */
  where(...conditions: Condition[]): this;
  orderBy(path: string, direction?: "asc" | "desc"): this;
  limit(count: number): this;
  offset(count: number): this;
  /** The text and parameters, as `/v1/query` takes them. */
  build(): { query: string; parameters: Record<string, unknown> };
  /** The statement, with `@p0`-style placeholders for its values. */
  toCuteQL(): string;
  readonly parameters: Record<string, unknown>;
  /** Runs on the server the query was created from (`collection.query()`). */
  run(): Promise<QueryResult>;
  explain(): Promise<QueryPlan>;
}

export declare function field(path: string): Field;
export declare function and(...conditions: Condition[]): Condition;
export declare function or(...conditions: Condition[]): Condition;
export declare function not(condition: Condition): Condition;
/** Escapes `\`, `%` and `_` exactly as the .NET LINQ provider does. */
export declare function escapeLike(text: string): string;

export default CuteClient;
