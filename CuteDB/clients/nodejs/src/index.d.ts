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
  query(query: string, parameters?: Record<string, unknown>): Promise<QueryResult>;
  explain(query: string, parameters?: Record<string, unknown>): Promise<QueryPlan>;
  stats(): Promise<Stats>;
  compact(): Promise<number>;
}

export default CuteClient;
