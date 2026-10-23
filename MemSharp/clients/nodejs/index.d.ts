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

/**
 * An entry as a consumer-group history read can return it: `fields` is `null` when the entry was
 * trimmed from the stream while still pending.
 */
export interface MaybeTrimmedEntry {
  id: string;
  fields: Record<string, string> | null;
}

/**
 * Which streams to read: a key-to-id map, or a key / array of keys meaning `$` for `xread` and `>`
 * for `xreadGroup`.
 */
export type StreamsSpec = Record<string, string> | string | string[];

/** Entries per stream key. Only streams that returned something are present. */
export type StreamReply<E = StreamEntry> = Record<string, E[]>;

export interface LposOptions {
  /** Which match to start from; negative searches from the tail. 0 is an error. */
  rank?: number;
  /** Return an array of up to this many indices; 0 means every match. */
  count?: number;
  /** Compare at most this many elements. */
  maxLen?: number;
}

export interface ZStoreOptions {
  /** Multiply each source's scores, in key order. */
  weights?: number[];
  /** How scores of a member present in several sources combine. Defaults to `sum`. */
  aggregate?: 'sum' | 'min' | 'max' | 'SUM' | 'MIN' | 'MAX';
}

export interface XReadOptions {
  count?: number;
  /** Wait up to this many milliseconds for an entry; `0` waits forever. */
  blockMs?: number;
}

export interface XReadGroupOptions extends XReadOptions {
  /** Deliver without adding to the pending list. */
  noAck?: boolean;
}

/** The summary form of `XPENDING`. */
export interface PendingSummary {
  count: number;
  /** Lowest pending id, or `null` when nothing is pending. */
  min: string | null;
  /** Highest pending id, or `null` when nothing is pending. */
  max: string | null;
  /** Pending count per consumer. */
  consumers: Record<string, number>;
}

export interface XPendingRangeOptions {
  /** Defaults to `-`. */
  start?: string;
  /** Defaults to `+`. */
  end?: string;
  /** Defaults to 100. */
  count?: number;
  /** Only this consumer's entries. */
  consumer?: string;
  /** Only entries idle at least this many milliseconds. */
  idleMs?: number;
}

/** One row of the range form of `XPENDING`. */
export interface PendingEntry {
  id: string;
  consumer: string;
  /** Milliseconds since the entry was last delivered. */
  idleMs: number;
  /** How many times it has been delivered. */
  deliveries: number;
}

export interface XClaimOptions {
  /** Set the claimed entries' idle time, in milliseconds. */
  idleMs?: number;
  /** Set the last-delivery time as a Unix time in milliseconds. */
  timeMs?: number;
  /** Set the delivery count. */
  retryCount?: number;
  /** Claim ids even if they are not pending. */
  force?: boolean;
  /** Return ids only, and leave the delivery count alone. */
  justId?: boolean;
  lastId?: string;
}

export interface XAutoClaimResult<C> {
  /** Where to resume the sweep; `0-0` once it is complete. */
  next: string;
  claimed: C[];
  /** Ids that were pending but no longer exist in the stream; they are dropped. */
  deleted: string[];
}

/** One row of `XINFO GROUPS`. */
export interface GroupInfo {
  name: string;
  consumers: number;
  pending: number;
  'last-delivered-id': string;
  [field: string]: unknown;
}

/** One row of `XINFO CONSUMERS`. */
export interface ConsumerInfo {
  name: string;
  pending: number;
  /** Milliseconds since the consumer last read. */
  idle: number;
  [field: string]: unknown;
}

/** The reply to `XINFO STREAM`. */
export interface StreamInfo {
  length: number;
  'last-generated-id': string;
  groups: number;
  'first-entry': StreamEntry | null;
  'last-entry': StreamEntry | null;
  [field: string]: unknown;
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
  /** Length in UTF-8 bytes, not characters. */
  strlen(key: string): Promise<number>;
  /** Inclusive byte offsets. Rejects if the range would split a multi-byte character. */
  getrange(key: string, start: number, end: number): Promise<string>;
  /** Resolves with the new length in bytes. Zero-pads a short string. */
  setrange(key: string, offset: number, value: unknown): Promise<number>;
  incr(key: string, amount?: number): Promise<number>;
  incrByFloat(key: string, amount: number): Promise<number>;
  expire(key: string, seconds: number): Promise<boolean>;
  /** Remaining lifetime in seconds, or `null` if the key is permanent or absent. */
  ttl(key: string): Promise<number | null>;
  persist(key: string): Promise<boolean>;
  keys(pattern?: string): Promise<string[]>;
  /**
   * Iterates the keyspace a page at a time until the server returns a zero cursor. The cursor can
   * exceed 2^32 and is passed back verbatim.
   */
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
  /** The index of a match, or `null`. */
  lpos(key: string, element: unknown, options?: Omit<LposOptions, 'count'>): Promise<number | null>;
  /** With `count`, an array of indices (empty when nothing matches). */
  lpos(key: string, element: unknown, options: LposOptions & { count: number }): Promise<number[]>;

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
  /** These resolve with the result size; an empty result deletes `destination`. */
  sinterstore(destination: string, ...keys: string[]): Promise<number>;
  sunionstore(destination: string, ...keys: string[]): Promise<number>;
  sdiffstore(destination: string, ...keys: string[]): Promise<number>;

  // -- sorted sets -----------------------------------------------------------------------
  zadd(key: string, scores: Record<string, number>): Promise<number>;
  zrange(key: string, start?: number, stop?: number, options?: RangeOptions): Promise<string[] | ScoredMember[]>;
  zrangeByScore(key: string, low: number, high: number, options?: RangeOptions): Promise<string[] | ScoredMember[]>;
  zscore(key: string, member: string): Promise<number | null>;
  zcard(key: string): Promise<number>;
  /** A plain set among `keys` scores every member 1. Resolves with the result size. */
  zunionstore(destination: string, keys: string[], options?: ZStoreOptions): Promise<number>;
  zinterstore(destination: string, keys: string[], options?: ZStoreOptions): Promise<number>;

  // -- streams ---------------------------------------------------------------------------
  xadd(key: string, fields: Record<string, unknown>, options?: { maxLen?: number }): Promise<string>;
  xrange(key?: string, start?: string, end?: string, options?: { count?: number }): Promise<StreamEntry[]>;
  xlen(key: string): Promise<number>;
  /**
   * Resolves with `null` if nothing is new (or the block timed out). A blocked read holds the
   * connection: commands sent behind it wait for it.
   */
  xread(streams: StreamsSpec, options?: XReadOptions): Promise<StreamReply | null>;

  /** `id` defaults to `$`. Rejects with code `BUSYGROUP` if the group exists. */
  xgroupCreate(key: string, group: string, id?: string, options?: { mkStream?: boolean }): Promise<unknown>;
  xgroupDestroy(key: string, group: string): Promise<boolean>;
  xgroupCreateConsumer(key: string, group: string, consumer: string): Promise<boolean>;
  /** Resolves with the number of pending entries the consumer held. */
  xgroupDelConsumer(key: string, group: string, consumer: string): Promise<number>;
  /** `id` defaults to `$`. */
  xgroupSetId(key: string, group: string, id?: string): Promise<unknown>;
  /**
   * Keys without ids read `>`, resolving `null` when nothing is new. An explicit id re-reads the
   * consumer's own pending history, where a trimmed entry has `fields: null`.
   */
  xreadGroup(group: string, consumer: string, streams: StreamsSpec,
    options?: XReadGroupOptions): Promise<StreamReply<MaybeTrimmedEntry> | null>;
  xack(key: string, group: string, ...ids: string[]): Promise<number>;
  xpending(key: string, group: string): Promise<PendingSummary>;
  xpendingRange(key: string, group: string, options?: XPendingRangeOptions): Promise<PendingEntry[]>;
  xclaim(key: string, group: string, consumer: string, minIdleMs: number, ids: string[],
    options?: XClaimOptions & { justId?: false }): Promise<StreamEntry[]>;
  xclaim(key: string, group: string, consumer: string, minIdleMs: number, ids: string[],
    options: XClaimOptions & { justId: true }): Promise<string[]>;
  /** `start` defaults to `0-0`. */
  xautoClaim(key: string, group: string, consumer: string, minIdleMs: number, start?: string,
    options?: { count?: number; justId?: false }): Promise<XAutoClaimResult<StreamEntry>>;
  xautoClaim(key: string, group: string, consumer: string, minIdleMs: number, start: string,
    options: { count?: number; justId: true }): Promise<XAutoClaimResult<string>>;
  xinfoGroups(key: string): Promise<GroupInfo[]>;
  xinfoConsumers(key: string, group: string): Promise<ConsumerInfo[]>;
  xinfoStream(key: string): Promise<StreamInfo>;

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
  /** Every section with no arguments, or only the named ones (`server`, `memory`, `keyspace`, ...). */
  info(...sections: string[]): Promise<Record<string, string>>;
  /** Patterns default to `*`. There is no `configSet`: the server refuses `CONFIG SET`. */
  configGet(...patterns: string[]): Promise<Record<string, string>>;
  configResetStat(): Promise<unknown>;
  /** Approximate bytes, or `null` if the key is absent. */
  memoryUsage(key: string, options?: { samples?: number }): Promise<number | null>;
  dbsize(): Promise<number>;
  flushdb(): Promise<unknown>;
  save(): Promise<unknown>;
}
