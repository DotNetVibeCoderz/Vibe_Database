'use strict';

const net = require('node:net');
const { EventEmitter } = require('node:events');

const {
  MemSharpError,
  WrongTypeError,
  ConnectionClosedError,
  encodeCommand,
  Decoder,
  flatten,
  unflatten,
} = require('./resp');

/**
 * A connection to a MemSharp server.
 *
 * Commands return promises and are matched to replies by order, which is what RESP guarantees. A
 * queue of pending resolvers rather than a request id, because the protocol has no id to correlate
 * on - the Nth reply belongs to the Nth request, full stop.
 *
 * @example
 * const db = new MemSharpClient({ port: 6380 });
 * await db.connect();
 * await db.set('symbol:BTC', '68350.25');
 * console.log(await db.get('symbol:BTC'));
 * await db.close();
 */
class MemSharpClient extends EventEmitter {
  /**
   * @param {object} [options]
   * @param {string} [options.host='127.0.0.1']
   * @param {number} [options.port=6380]
   * @param {number} [options.timeout=10000] Connect timeout in milliseconds.
   */
  constructor(options = {}) {
    super();
    this.host = options.host ?? '127.0.0.1';
    this.port = options.port ?? 6380;
    this.timeout = options.timeout ?? 10000;

    this._socket = null;
    this._decoder = new Decoder();
    this._pending = [];
    this._subscribed = false;
  }

  // -- connection ------------------------------------------------------------------------

  /** Open the connection. */
  connect() {
    if (this._socket) return Promise.resolve(this);

    return new Promise((resolve, reject) => {
      const socket = net.createConnection({ host: this.host, port: this.port });

      // The workload is many small request/response round-trips, which is exactly what Nagle's
      // coalescing delay ruins.
      socket.setNoDelay(true);
      socket.setTimeout(this.timeout);

      const onError = (error) => {
        socket.destroy();
        reject(error);
      };

      socket.once('error', onError);
      socket.once('connect', () => {
        socket.setTimeout(0);
        socket.off('error', onError);

        socket.on('data', (chunk) => this._onData(chunk));
        socket.on('error', (error) => this._fail(error));
        socket.on('close', () => this._fail(new ConnectionClosedError()));

        this._socket = socket;
        resolve(this);
      });
    });
  }

  /** Close the connection. Safe to call more than once. */
  async close() {
    if (!this._socket) return;

    const socket = this._socket;
    this._socket = null;
    await new Promise((resolve) => socket.end(resolve));
    socket.destroy();
  }

  _onData(chunk) {
    this._decoder.feed(chunk);

    for (;;) {
      let taken;
      try {
        taken = this._decoder.take();
      } catch (error) {
        this._fail(error);
        return;
      }
      if (!taken.ready) return;

      const value = taken.value;

      // While subscribed the server pushes messages that answer no request, so they must not be
      // matched against the pending queue - doing so would hand a later command someone else's
      // message and desynchronise every reply after it.
      if (this._subscribed && Array.isArray(value) && value.length >= 3) {
        const kind = value[0];
        if (kind === 'message') {
          this.emit('message', value[1], value[2]);
          continue;
        }
        if (kind === 'pmessage' && value.length >= 4) {
          this.emit('message', value[2], value[3], value[1]);
          continue;
        }
      }

      const waiter = this._pending.shift();
      if (!waiter) continue;

      if (value instanceof Error && waiter.raiseErrors) waiter.reject(value);
      else waiter.resolve(value);
    }
  }

  _fail(error) {
    const waiting = this._pending;
    this._pending = [];
    for (const waiter of waiting) waiter.reject(error);
    if (this.listenerCount('error') > 0) this.emit('error', error);
  }

  // -- command plumbing ------------------------------------------------------------------

  /** Send one command and resolve with its reply. Error replies reject. */
  async execute(...args) {
    await this.connect();
    return this._send(args, true);
  }

  /**
   * Send several commands in one write and resolve with every reply.
   *
   * One round-trip for the whole batch instead of one each. Errors are returned in place rather
   * than rejecting, so one failing command does not hide the replies to the others.
   *
   * @param {Array<Array<*>>} commands
   */
  async pipeline(commands) {
    if (commands.length === 0) return [];
    await this.connect();

    const replies = commands.map((command) => this._enqueue(false));
    this._socket.write(Buffer.concat(commands.map(encodeCommand)));
    return Promise.all(replies);
  }

  _send(args, raiseErrors) {
    const reply = this._enqueue(raiseErrors);
    this._socket.write(encodeCommand(args));
    return reply;
  }

  _enqueue(raiseErrors) {
    return new Promise((resolve, reject) => {
      this._pending.push({ resolve, reject, raiseErrors });
    });
  }

  // -- keys ------------------------------------------------------------------------------

  /** Round-trip the server. */
  ping() {
    return this.execute('PING');
  }

  /**
   * Store a string.
   * @param {string} key
   * @param {*} value
   * @param {object} [options]
   * @param {number} [options.ex] Lifetime in seconds.
   * @param {boolean} [options.nx] Only write if the key is absent.
   */
  async set(key, value, options = {}) {
    const args = ['SET', key, value];
    if (options.ex !== undefined) args.push('PX', Math.round(options.ex * 1000));
    if (options.nx) args.push('NX');
    return (await this.execute(...args)) !== null;
  }

  /** Read a string, or null if the key is absent. */
  get(key) {
    return this.execute('GET', key);
  }

  /** Read several strings. Missing keys come back as null in position. */
  mget(...keys) {
    return this.execute('MGET', ...keys);
  }

  /** Write several key/value pairs. */
  mset(object) {
    return this.execute('MSET', ...flatten(object));
  }

  /** Remove keys. Resolves with how many existed. */
  del(...keys) {
    return this.execute('DEL', ...keys);
  }

  /** Count how many of the given keys exist. */
  exists(...keys) {
    return this.execute('EXISTS', ...keys);
  }

  /** The kind of value a key holds, or 'none'. */
  type(key) {
    return this.execute('TYPE', key);
  }

  /** Atomically add to an integer, treating a missing key as 0. */
  incr(key, amount = 1) {
    return this.execute('INCRBY', key, amount);
  }

  /** Atomically add to a floating-point value. */
  async incrByFloat(key, amount) {
    return Number(await this.execute('INCRBYFLOAT', key, amount));
  }

  /** Set a lifetime in seconds. Resolves false if the key is absent. */
  async expire(key, seconds) {
    return (await this.execute('PEXPIRE', key, Math.round(seconds * 1000))) === 1;
  }

  /** Remaining lifetime in seconds, or null if the key is permanent or absent. */
  async ttl(key) {
    const milliseconds = await this.execute('PTTL', key);
    return milliseconds < 0 ? null : milliseconds / 1000;
  }

  /** Clear a key's expiry. */
  async persist(key) {
    return (await this.execute('PERSIST', key)) === 1;
  }

  /** Every key matching a glob. Prefer `scan` on a large keyspace. */
  keys(pattern = '*') {
    return this.execute('KEYS', pattern);
  }

  /**
   * Iterate the keyspace a page at a time, until the server returns a zero cursor.
   * @returns {AsyncGenerator<string>}
   */
  async *scan(pattern = '*', count = 500) {
    let cursor = 0;
    for (;;) {
      const [next, page] = await this.execute('SCAN', cursor, 'MATCH', pattern, 'COUNT', count);
      yield* page;
      cursor = Number(next);
      if (cursor === 0) return;
    }
  }

  // -- lists -----------------------------------------------------------------------------

  /** Push onto the head of a list. Resolves with the new length. */
  lpush(key, ...values) {
    return this.execute('LPUSH', key, ...values);
  }

  /** Push onto the tail of a list. Resolves with the new length. */
  rpush(key, ...values) {
    return this.execute('RPUSH', key, ...values);
  }

  /** Remove and return the head. */
  lpop(key) {
    return this.execute('LPOP', key);
  }

  /** Remove and return the tail. */
  rpop(key) {
    return this.execute('RPOP', key);
  }

  /** Elements in an inclusive index range. Negative indices count back from the end. */
  lrange(key, start = 0, stop = -1) {
    return this.execute('LRANGE', key, start, stop);
  }

  /** Number of elements in a list. */
  llen(key) {
    return this.execute('LLEN', key);
  }

  /** Keep only an index range - the way to cap a feed at a fixed length. */
  ltrim(key, start, stop) {
    return this.execute('LTRIM', key, start, stop);
  }

  // -- hashes ----------------------------------------------------------------------------

  /** Set fields from an object. Resolves with how many were new. */
  hset(key, fields) {
    return this.execute('HSET', key, ...flatten(fields));
  }

  /** Read one field. */
  hget(key, field) {
    return this.execute('HGET', key, field);
  }

  /** Every field and value, as an object. */
  async hgetall(key) {
    return unflatten(await this.execute('HGETALL', key));
  }

  /** Remove fields. Resolves with how many existed. */
  hdel(key, ...fields) {
    return this.execute('HDEL', key, ...fields);
  }

  /** Atomically add to an integer field. */
  hincrby(key, field, amount = 1) {
    return this.execute('HINCRBY', key, field, amount);
  }

  // -- sets ------------------------------------------------------------------------------

  /** Add members. Resolves with how many were new. */
  sadd(key, ...members) {
    return this.execute('SADD', key, ...members);
  }

  /** Remove members. Resolves with how many were present. */
  srem(key, ...members) {
    return this.execute('SREM', key, ...members);
  }

  /** Every member of a set. */
  smembers(key) {
    return this.execute('SMEMBERS', key);
  }

  /** True if a set contains a member. */
  async sismember(key, member) {
    return (await this.execute('SISMEMBER', key, member)) === 1;
  }

  // -- sorted sets -----------------------------------------------------------------------

  /**
   * Add scored members.
   * @param {string} key
   * @param {Record<string, number>} scores member to score
   */
  zadd(key, scores) {
    const args = [];
    for (const [member, score] of Object.entries(scores)) args.push(score, member);
    return this.execute('ZADD', key, ...args);
  }

  /**
   * Members in a rank range.
   * @param {object} [options]
   * @param {boolean} [options.desc] Highest score first.
   * @param {boolean} [options.withScores] Return `{member, score}` objects.
   */
  async zrange(key, start = 0, stop = -1, options = {}) {
    const command = options.desc ? 'ZREVRANGE' : 'ZRANGE';
    const args = [command, key, start, stop];
    if (options.withScores) args.push('WITHSCORES');

    const reply = await this.execute(...args);
    return options.withScores ? pairScores(reply) : reply;
  }

  /** Members whose score falls in an inclusive range. */
  async zrangeByScore(key, low, high, options = {}) {
    const args = ['ZRANGEBYSCORE', key, low, high];
    if (options.withScores) args.push('WITHSCORES');

    const reply = await this.execute(...args);
    return options.withScores ? pairScores(reply) : reply;
  }

  /** A member's score, or null. */
  async zscore(key, member) {
    const score = await this.execute('ZSCORE', key, member);
    return score === null ? null : Number(score);
  }

  /** Number of members. */
  zcard(key) {
    return this.execute('ZCARD', key);
  }

  // -- streams ---------------------------------------------------------------------------

  /** Append an entry. Resolves with the assigned `ms-seq` id. */
  xadd(key, fields, options = {}) {
    const args = ['XADD', key];
    if (options.maxLen !== undefined) args.push('MAXLEN', options.maxLen);
    args.push('*', ...flatten(fields));
    return this.execute(...args);
  }

  /** Entries in an id range, oldest first, as `{id, fields}`. */
  async xrange(key, start = '-', end = '+', options = {}) {
    const args = ['XRANGE', key, start, end];
    if (options.count !== undefined) args.push('COUNT', options.count);

    const rows = await this.execute(...args);
    return rows.map(([id, flat]) => ({ id, fields: unflatten(flat) }));
  }

  /** Number of entries. */
  xlen(key) {
    return this.execute('XLEN', key);
  }

  // -- time series -----------------------------------------------------------------------

  /** Create a time series, optionally capped at `retention` samples. */
  tsCreate(key, retention = 0) {
    return this.execute('TS.CREATE', key, 'RETENTION', retention);
  }

  /** Append a sample. Resolves with the timestamp written. */
  tsAdd(key, value, timestamp = null) {
    return this.execute('TS.ADD', key, timestamp === null ? '*' : timestamp, value);
  }

  /** Samples in an inclusive timestamp range, as `{timestamp, value}`. */
  async tsRange(key, start = '-', end = '+') {
    const rows = await this.execute('TS.RANGE', key, start, end);
    return rows.map(([timestamp, value]) => ({ timestamp: Number(timestamp), value: Number(value) }));
  }

  /**
   * Fold a range into fixed-width buckets.
   * @param {string} how avg, min, max, sum, count, first or last.
   */
  async tsAggregate(key, start, end, bucketMs, how = 'avg') {
    const rows = await this.execute('TS.AGGREGATE', key, start, end, bucketMs, how);
    return rows.map(([timestamp, value]) => ({ timestamp: Number(timestamp), value: Number(value) }));
  }

  // -- pub/sub ---------------------------------------------------------------------------

  /** Publish a message. Resolves with how many subscribers received it. */
  publish(channel, message) {
    return this.execute('PUBLISH', channel, message);
  }

  /**
   * Subscribe to channels and emit `message` events.
   *
   * This takes over the connection: the server may push a message between any request and its
   * reply, so a subscribed client cannot also run ordinary commands. Use a second client.
   *
   * @fires MemSharpClient#message with `(channel, message, pattern?)`
   */
  async subscribe(...channels) {
    await this.connect();
    this._subscribed = true;
    return this._send(['SUBSCRIBE', ...channels], true);
  }

  /** Subscribe to channel glob patterns. */
  async psubscribe(...patterns) {
    await this.connect();
    this._subscribed = true;
    return this._send(['PSUBSCRIBE', ...patterns], true);
  }

  // -- query and server ------------------------------------------------------------------

  /**
   * Run a SELECT against the keyspace and resolve with rows as objects.
   *
   * The server sends the column names as the first row; this pairs them with each row so callers
   * work with names rather than positions.
   */
  async sql(query) {
    const reply = await this.execute('SQL', query);
    if (typeof reply === 'number' || !Array.isArray(reply) || reply.length === 0) return [];

    const [columns, ...rows] = reply;
    return rows.map((row) => {
      const record = {};
      columns.forEach((column, index) => {
        record[column] = row[index];
      });
      return record;
    });
  }

  /** Run a DELETE against the keyspace and resolve with the number of rows removed. */
  async sqlDelete(query) {
    const reply = await this.execute('SQL', query);
    return typeof reply === 'number' ? reply : 0;
  }

  /** Server statistics, parsed into an object. Section headings are dropped. */
  async info() {
    const text = await this.execute('INFO');
    const result = {};
    for (const line of text.split('\n')) {
      const trimmed = line.trim();
      if (!trimmed || trimmed.startsWith('#')) continue;
      const colon = trimmed.indexOf(':');
      if (colon > 0) result[trimmed.slice(0, colon)] = trimmed.slice(colon + 1);
    }
    return result;
  }

  /** Number of keys in the database. */
  dbsize() {
    return this.execute('DBSIZE');
  }

  /** Remove every key. */
  flushdb() {
    return this.execute('FLUSHDB');
  }

  /** Write a snapshot synchronously. */
  save() {
    return this.execute('SAVE');
  }
}

function pairScores(flat) {
  const result = [];
  for (let i = 0; i + 1 < flat.length; i += 2) {
    result.push({ member: flat[i], score: Number(flat[i + 1]) });
  }
  return result;
}

module.exports = { MemSharpClient, MemSharpError, WrongTypeError, ConnectionClosedError };
