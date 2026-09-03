/**
 * Type definitions for the MemSharp Node.js client.
 *
 * MemSharp is an in-memory database for .NET that speaks RESP. Start a server with
 * `memsharp serve --port 6380`.
 */

import { EventEmitter } from 'node:events';

/** An error reply from the server. */
export class MemSharpError extends Error {
  /** The leading token of the reply, e.g. `WRONGTYPE`. Part of the wire contract. */
  code: string;
  /** The message after the code. */
  detail: string;
  constructor(code: string, message: string);
}

/** A command was applied to a key holding a different type. */
export class WrongTypeError extends MemSharpError {
  constructor(message: string);
}

/** The connection closed before a complete reply arrived. */
export class ConnectionClosedError extends MemSharpError {
  constructor(message?: string);
}

export interface MemSharpClientOptions {
  /** Defaults to `127.0.0.1`. */
  host?: string;
  /** Defaults to `6380`. */
  port?: number;
  /** Connect timeout in milliseconds. Defaults to `10000`. */
  timeout?: number;
}

export interface SetOptions {
  /** Lifetime in seconds. */
  ex?: number;
  /** Only write if the key is absent. */
  nx?: boolean;
}

export interface RangeOptions {
  /** Highest score first. */
  desc?: boolean;
  /** Return `{ member, score }` objects rather than bare members. */
  withScores?: boolean;
}

/** A sorted set member with its score. */
export interface ScoredMember {
  member: string;
  score: number;
}

/** One entry of a stream. */
export interface StreamEntry {
  /** The `ms-seq` id. */
  id: string;
  fields: Record<string, string>;
}

/** One time-series observation. */
export interface Sample {
  timestamp: number;
  value: number;
}

/** How a time-series range is folded into buckets. */
export type Aggregation = 'avg' | 'min' | 'max' | 'sum' | 'count' | 'first' | 'last';

/** A row from a `SELECT`, keyed by column name. A null cell is a SQL NULL. */
export type SqlRow = Record<string, string | null>;

/**
 * A connection to a MemSharp server.
 *
 * Commands return promises and are matched to replies by order, which is what RESP guarantees.
 *
 * Subscribing takes over the connection: the server may push a message between any request and its
 * reply, so a subscribed client cannot also run ordinary commands. Use a second client.
 */
export class MemSharpClient extends EventEmitter {
  constructor(options?: MemSharpClientOptions);

  readonly host: string;
  readonly port: number;
  readonly timeout: number;

  /** Emitted for each pub/sub message. `pattern` is set only for a pattern subscription. */
  on(event: 'message', listener: (channel: string, message: string, pattern?: string) => void): this;
  on(event: 'error', listener: (error: Error) => void): this;

  connect(): Promise<this>;
  close(): Promise<void>;

  /** Sends one command and resolves with its reply. An error reply rejects. */
  execute(...args: unknown[]): Promise<unknown>;

  /**
   * Sends several commands in one write — one round-trip for the whole batch.
   *
   * Error replies are returned in place as `Error` values rather than rejecting, so one failing
   * command does not hide the replies to the others.
   */
  pipeline(commands: unknown[][]): Promise<unknown[]>;

  // -- keys ------------------------------------------------------------------------------
  ping(): Promise<string>;
  set(key: string, value: unknown, options?: SetOptions): Promise<boolean>;
  get(key: string): Promise<string | null>;
  mget(...keys: string[]): Promise<(string | null)[]>;
  mset(values: Record<string, unknown>): Promise<unknown>;
  del(...keys: string[]): Promise<number>;
  exists(...keys: string[]): Promise<number>;
  type(key: string): Promise<string>;
  incr(key: string, amount?: number): Promise<number>;
  incrByFloat(key: string, amount: number): Promise<number>;
  expire(key: string, seconds: number): Promise<boolean>;
  /** Remaining lifetime in seconds, or `null` if the key is permanent or absent. */
  ttl(key: string): Promise<number | null>;
  persist(key: string): Promise<boolean>;
  keys(pattern?: string): Promise<string[]>;
  /** Iterates the keyspace a page at a time until the server returns a zero cursor. */
  scan(pattern?: string, count?: number): AsyncGenerator<string, void, undefined>;

  // -- lists -----------------------------------------------------------------------------
  lpush(key: string, ...values: unknown[]): Promise<number>;
  rpush(key: string, ...values: unknown[]): Promise<number>;
  lpop(key: string): Promise<string | null>;
  rpop(key: string): Promise<string | null>;
  /** Negative indices count back from the end, so `(0, -1)` is the whole list. */
  lrange(key: string, start?: number, stop?: number): Promise<string[]>;
  llen(key: string): Promise<number>;
  ltrim(key: string, start: number, stop: number): Promise<unknown>;

  // -- hashes ----------------------------------------------------------------------------
  hset(key: string, fields: Record<string, unknown>): Promise<number>;
  hget(key: string, field: string): Promise<string | null>;
  hgetall(key: string): Promise<Record<string, string>>;
  hdel(key: string, ...fields: string[]): Promise<number>;
  hincrby(key: string, field: string, amount?: number): Promise<number>;

  // -- sets ------------------------------------------------------------------------------
  sadd(key: string, ...members: unknown[]): Promise<number>;
  srem(key: string, ...members: unknown[]): Promise<number>;
  smembers(key: string): Promise<string[]>;
  sismember(key: string, member: unknown): Promise<boolean>;

  // -- sorted sets -----------------------------------------------------------------------
  zadd(key: string, scores: Record<string, number>): Promise<number>;
  zrange(key: string, start?: number, stop?: number, options?: RangeOptions): Promise<string[] | ScoredMember[]>;
  zrangeByScore(key: string, low: number, high: number, options?: RangeOptions): Promise<string[] | ScoredMember[]>;
  zscore(key: string, member: string): Promise<number | null>;
  zcard(key: string): Promise<number>;

  // -- streams ---------------------------------------------------------------------------
  xadd(key: string, fields: Record<string, unknown>, options?: { maxLen?: number }): Promise<string>;
  xrange(key?: string, start?: string, end?: string, options?: { count?: number }): Promise<StreamEntry[]>;
  xlen(key: string): Promise<number>;

  // -- time series -----------------------------------------------------------------------
  tsCreate(key: string, retention?: number): Promise<unknown>;
  /** Pass `null` for `timestamp` to have the server stamp the sample. */
  tsAdd(key: string, value: number, timestamp?: number | null): Promise<number>;
  tsRange(key: string, start?: number | string, end?: number | string): Promise<Sample[]>;
  tsAggregate(key: string, start: number, end: number, bucketMs: number, how?: Aggregation): Promise<Sample[]>;

  // -- pub/sub ---------------------------------------------------------------------------
  publish(channel: string, message: unknown): Promise<number>;
  subscribe(...channels: string[]): Promise<unknown>;
  psubscribe(...patterns: string[]): Promise<unknown>;

  // -- query and server ------------------------------------------------------------------
  /** Runs a `SELECT` against the keyspace. Rows come back keyed by column name. */
  sql(query: string): Promise<SqlRow[]>;
  /** Runs a `DELETE` against the keyspace and resolves with the number of rows removed. */
  sqlDelete(query: string): Promise<number>;
  info(): Promise<Record<string, string>>;
  dbsize(): Promise<number>;
  flushdb(): Promise<unknown>;
  save(): Promise<unknown>;
}
