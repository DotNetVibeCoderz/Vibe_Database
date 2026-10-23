'use strict';

/**
 * Integration tests for the Node.js client.
 *
 * These run against a live server rather than a mock, because the thing worth testing is that the
 * wire encoding matches what MemSharp actually sends back. Start one first:
 *
 *   memsharp serve --port 6391 --quiet
 *   node clients/nodejs/test/client.test.js
 */

const { MemSharpClient, WrongTypeError } = require('../index');

const PORT = Number(process.env.MEMSHARP_TEST_PORT || 6391);
const failures = [];

function check(name, condition, detail = '') {
  const status = condition ? '  ok  ' : ' FAIL ';
  console.log(`${status} ${name}${detail ? `  [${detail}]` : ''}`);
  if (!condition) failures.push(name);
}

function same(a, b) {
  return JSON.stringify(a) === JSON.stringify(b);
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Resolves with the error a promise rejected with, or null if it resolved. */
async function rejects(promise) {
  try {
    await promise;
    return null;
  } catch (error) {
    return error;
  }
}

async function main() {
  const db = new MemSharpClient({ port: PORT });
  await db.connect();
  await db.flushdb();

  // -- strings and counters --------------------------------------------------------
  check('ping', (await db.ping()) === 'PONG');
  await db.set('k', 'v');
  check('set/get', (await db.get('k')) === 'v');
  check('get missing', (await db.get('absent')) === null);
  check('incr', (await db.incr('n', 41)) === 41 && (await db.incr('n')) === 42);
  check('incrByFloat', Math.abs((await db.incrByFloat('f', 1.5)) - 1.5) < 1e-9);
  check('mget order', same(await db.mget('k', 'absent', 'n'), ['v', null, '42']));

  await db.mset({ a: '1', b: '2' });
  check('mset', same(await db.mget('a', 'b'), ['1', '2']));
  check('exists', (await db.exists('a', 'b', 'nope')) === 2);
  check('type', (await db.type('k')) === 'string');
  check('del', (await db.del('a', 'b')) === 2);

  // -- expiry ----------------------------------------------------------------------
  await db.set('temp', 'v', { ex: 60 });
  const ttl = await db.ttl('temp');
  check('ttl set', ttl !== null && ttl > 55 && ttl <= 60, String(ttl));
  check('ttl permanent', (await db.ttl('k')) === null);
  check('persist', (await db.persist('temp')) && (await db.ttl('temp')) === null);

  await db.set('brief', 'v', { ex: 0.05 });
  await new Promise((resolve) => setTimeout(resolve, 200));
  check('expiry evicts', (await db.get('brief')) === null);

  check('set nx', (await db.set('k', 'other', { nx: true })) === false && (await db.get('k')) === 'v');

  // -- lists -----------------------------------------------------------------------
  await db.rpush('l', 'a', 'b', 'c');
  check('rpush/lrange', same(await db.lrange('l'), ['a', 'b', 'c']));
  await db.lpush('l', 'z');
  check('lpush', same(await db.lrange('l', 0, 0), ['z']));
  check('llen', (await db.llen('l')) === 4);
  check('lpop', (await db.lpop('l')) === 'z');
  check('rpop', (await db.rpop('l')) === 'c');
  await db.ltrim('l', 0, 0);
  check('ltrim', same(await db.lrange('l'), ['a']));

  await db.rpush('r', '1', '2', '3');
  check('negative index range', same(await db.lrange('r', -2, -1), ['2', '3']));

  // -- hashes ----------------------------------------------------------------------
  await db.hset('h', { name: 'Kang Fadhil', desk: 'Jakarta' });
  check('hset/hgetall', same(await db.hgetall('h'), { name: 'Kang Fadhil', desk: 'Jakarta' }));
  check('hget', (await db.hget('h', 'desk')) === 'Jakarta');
  check('hincrby', (await db.hincrby('h', 'fills', 5)) === 5);
  check('hdel', (await db.hdel('h', 'desk')) === 1 && (await db.hget('h', 'desk')) === null);

  // -- sets ------------------------------------------------------------------------
  await db.sadd('s', 'x', 'y', 'x');
  check('sadd dedupes', (await db.smembers('s')).sort().join(',') === 'x,y');
  check('sismember', (await db.sismember('s', 'x')) && !(await db.sismember('s', 'q')));
  check('srem', (await db.srem('s', 'x')) === 1);

  // -- sorted sets -----------------------------------------------------------------
  await db.zadd('z', { low: 1.0, high: 3.0, mid: 2.0 });
  check('zadd/zrange', same(await db.zrange('z'), ['low', 'mid', 'high']));
  check('zrange desc', same(await db.zrange('z', 0, -1, { desc: true }), ['high', 'mid', 'low']));

  const scored = await db.zrange('z', 0, -1, { withScores: true });
  check('zrange withScores', same(scored[0], { member: 'low', score: 1 }), JSON.stringify(scored[0]));
  check('zrangeByScore', same(await db.zrangeByScore('z', 1.5, 2.5), ['mid']));
  check('zscore', (await db.zscore('z', 'high')) === 3);
  check('zscore missing', (await db.zscore('z', 'nope')) === null);
  check('zcard', (await db.zcard('z')) === 3);

  // -- streams ---------------------------------------------------------------------
  const first = await db.xadd('stream', { sym: 'BTC', qty: '5' });
  await db.xadd('stream', { sym: 'ETH', qty: '12' });
  check('xadd returns id', typeof first === 'string' && first.includes('-'), first);
  check('xlen', (await db.xlen('stream')) === 2);

  const entries = await db.xrange('stream');
  check('xrange fields', same(entries[0].fields, { sym: 'BTC', qty: '5' }), JSON.stringify(entries[0].fields));

  for (let i = 0; i < 50; i++) await db.xadd('capped', { n: String(i) }, { maxLen: 10 });
  check('xadd maxLen', (await db.xlen('capped')) === 10);

  // -- time series -----------------------------------------------------------------
  await db.tsCreate('ts', 1000);
  for (let i = 0; i < 20; i++) await db.tsAdd('ts', i, i * 100);

  const samples = await db.tsRange('ts', 0, 10000);
  check('tsRange', samples.length === 20 && samples[0].timestamp === 0, JSON.stringify(samples[0]));

  const buckets = await db.tsAggregate('ts', 0, 10000, 500, 'max');
  check('tsAggregate', buckets.length === 4 && buckets[0].value === 4, JSON.stringify(buckets));

  // -- SQL -------------------------------------------------------------------------
  for (let i = 0; i < 20; i++) await db.set(`order:${i}`, 'x'.repeat(i));

  const rows = await db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%' AND size > 15 ORDER BY size DESC LIMIT 3");
  check('sql rows are objects', rows.length === 3 && rows[0].size === '19', JSON.stringify(rows[0]));
  check('sql column names', same(Object.keys(rows[0]).sort(), ['key', 'size']));
  check('sql delete', (await db.sqlDelete("DELETE FROM keys WHERE key LIKE 'order:1%'")) === 11);

  // -- errors ----------------------------------------------------------------------
  try {
    await db.get('l');
    check('wrongtype rejects', false);
  } catch (error) {
    check('wrongtype rejects', error instanceof WrongTypeError && error.code === 'WRONGTYPE', error.detail);
  }

  // An error must not desynchronise the connection.
  check('connection survives an error', (await db.ping()) === 'PONG');

  // -- pipelining ------------------------------------------------------------------
  const batch = [];
  for (let i = 0; i < 500; i++) batch.push(['SET', `p${i}`, String(i)]);

  const replies = await db.pipeline(batch);
  check('pipeline replies', replies.length === 500 && replies.every((r) => r === 'OK'));
  check('pipeline stored', (await db.get('p499')) === '499');

  const mixed = await db.pipeline([['PING'], ['NOSUCHCOMMAND'], ['PING']]);
  check('pipeline returns errors in place',
    mixed[0] === 'PONG' && mixed[1] instanceof Error && mixed[2] === 'PONG');

  // -- scan ------------------------------------------------------------------------
  const found = [];
  for await (const key of db.scan('p*', 64)) found.push(key);
  check('scan pages', found.length === 500, String(found.length));

  // -- pub/sub ---------------------------------------------------------------------
  const subscriber = new MemSharpClient({ port: PORT });
  await subscriber.connect();

  const received = [];
  const delivered = new Promise((resolve) => {
    subscriber.on('message', (channel, message) => {
      // The warmup publishes below only detect that the subscription registered.
      if (message === 'warmup') return;
      received.push(message);
      if (received.length === 2) resolve();
    });
  });

  await subscriber.subscribe('news');

  const deadline = Date.now() + 5000;
  while ((await db.publish('news', 'warmup')) === 0 && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 50));
  }

  await db.publish('news', 'one');
  await db.publish('news', 'two');
  await Promise.race([delivered, new Promise((resolve) => setTimeout(resolve, 5000))]);

  check('pubsub delivers', same(received, ['one', 'two']), JSON.stringify(received));
  await subscriber.close();

  // -- server ----------------------------------------------------------------------
  const info = await db.info();
  check('info parses', info.product === 'MemSharp' && 'keys' in info);
  check('dbsize', (await db.dbsize()) > 0);

  const keyspace = await db.info('keyspace');
  check('info section', (keyspace.db0 ?? '').startsWith('keys=') && !('redis_version' in keyspace),
    JSON.stringify(keyspace));
  const two = await db.info('server', 'memory');
  check('info several sections', 'memsharp_version' in two && 'used_memory' in two);

  const config = await db.configGet();
  check('configGet all', config['maxmemory-policy'] === 'noeviction' && 'shards' in config);
  check('configGet pattern', same(await db.configGet('maxmemory', 'nope'), { maxmemory: '0' }));
  check('config set refused', await rejects(db.execute('CONFIG', 'SET', 'maxmemory', '1')));
  check('configResetStat', (await db.configResetStat()) === 'OK');

  check('memoryUsage', (await db.memoryUsage('h')) > 0 && (await db.memoryUsage('absent')) === null);
  check('memoryUsage samples', (await db.memoryUsage('r', { samples: 2 })) > 0);

  // -- 1.1: strings ----------------------------------------------------------------
  await db.set('utf', 'héllo');
  check('strlen counts bytes', (await db.strlen('utf')) === 6);
  check('getrange', (await db.getrange('utf', 0, 2)) === 'hé' && (await db.getrange('utf', -3, -1)) === 'llo');
  check('getrange missing key', (await db.getrange('absent', 0, 5)) === '');
  const split = await rejects(db.getrange('utf', 0, 1));
  check('getrange refuses split char', split && split.detail.includes('multi-byte'), split && split.detail);
  check('setrange pads', (await db.setrange('sr', 3, 'ab')) === 5 && (await db.get('sr')) === '\0\0\0ab');
  check('setrange overwrites', (await db.setrange('utf', 3, 'LO')) === 6 && (await db.get('utf')) === 'héLOo');

  // -- 1.1: lists ------------------------------------------------------------------
  await db.rpush('pos', 'a', 'b', 'c', 'b', 'b');
  check('lpos', (await db.lpos('pos', 'b')) === 1 && (await db.lpos('pos', 'q')) === null);
  check('lpos rank', (await db.lpos('pos', 'b', { rank: -1 })) === 4 && (await db.lpos('pos', 'b', { rank: 2 })) === 3);
  check('lpos count', same(await db.lpos('pos', 'b', { count: 0 }), [1, 3, 4])
    && same(await db.lpos('pos', 'b', { count: 2 }), [1, 3]));
  check('lpos count none', same(await db.lpos('pos', 'q', { count: 1 }), []));
  check('lpos maxLen', same(await db.lpos('pos', 'b', { count: 0, maxLen: 3 }), [1]));

  // -- 1.1: set and sorted-set stores ----------------------------------------------
  await db.sadd('sa', '1', '2', '3');
  await db.sadd('sb', '2', '3', '4');
  check('sinterstore', (await db.sinterstore('si', 'sa', 'sb')) === 2
    && (await db.smembers('si')).sort().join(',') === '2,3');
  check('sunionstore', (await db.sunionstore('su', 'sa', 'sb')) === 4);
  check('sdiffstore', (await db.sdiffstore('sd', 'sa', 'sb')) === 1 && same(await db.smembers('sd'), ['1']));
  check('empty store deletes', (await db.sinterstore('si', 'sa', 'absent')) === 0 && (await db.exists('si')) === 0);

  await db.zadd('z1', { x: 1, y: 2 });
  await db.zadd('z2', { y: 10, w: 20 });
  check('zunionstore', (await db.zunionstore('zu', ['z1', 'z2'])) === 3 && (await db.zscore('zu', 'y')) === 12);
  check('zunionstore weights/max', (await db.zunionstore('zw', ['z1', 'z2'], { weights: [1, 2], aggregate: 'max' })) === 3
    && same(await db.zrange('zw', 0, -1, { withScores: true }),
      [{ member: 'x', score: 1 }, { member: 'y', score: 20 }, { member: 'w', score: 40 }]));
  check('zinterstore', (await db.zinterstore('zi', ['z1', 'z2'], { aggregate: 'min' })) === 1
    && (await db.zscore('zi', 'y')) === 2);
  await db.sadd('plain', 'x');
  check('zinterstore plain set scores 1', (await db.zinterstore('zp', ['z1', 'plain'])) === 1
    && (await db.zscore('zp', 'x')) === 2);

  // -- 1.1: scan cursor ------------------------------------------------------------
  // The shard index rides in the cursor's high 32 bits, so any cursor past the first shard is above
  // 2^32. Walk the raw cursors to prove one was, then that scan() survives it.
  let cursor = '0';
  let largest = 0n;
  do {
    [cursor] = await db.execute('SCAN', cursor, 'COUNT', 1);
    if (BigInt(cursor) > largest) largest = BigInt(cursor);
  } while (cursor !== '0');
  check('scan cursor exceeds 2^32', largest > 2n ** 32n, String(largest));
  const p49 = [];
  for await (const key of db.scan('p49*', 1)) p49.push(key);
  check('scan follows large cursors', p49.length === 11, String(p49.length));

  // -- 1.1: xread ------------------------------------------------------------------
  await db.del('rs', 'rs2');
  const a = await db.xadd('rs', { n: '1' });
  const b = await db.xadd('rs', { n: '2' });
  const read = await db.xread({ rs: '0', rs2: '0' });
  check('xread from 0', same(read, { rs: [{ id: a, fields: { n: '1' } }, { id: b, fields: { n: '2' } }] }),
    JSON.stringify(read));
  check('xread count', same(await db.xread({ rs: '0' }, { count: 1 }), { rs: [{ id: a, fields: { n: '1' } }] }));
  check('xread after id', same(await db.xread({ rs: a }), { rs: [{ id: b, fields: { n: '2' } }] }));
  check('xread nothing new', (await db.xread('rs')) === null);

  let started = Date.now();
  check('xread block times out to null', (await db.xread('rs', { blockMs: 150 })) === null);
  check('xread block waited', Date.now() - started >= 100);

  // A second connection adds while the first is parked; only that XADD can end a BLOCK 0.
  const other = new MemSharpClient({ port: PORT });
  await other.connect();
  const addLater = (key) => sleep(300).then(() => other.xadd(key, { n: 'woke' }));

  started = Date.now();
  addLater('rs');
  const woken = await db.xread('rs', { blockMs: 0 });
  check('xread block woken by xadd', woken !== null && same(woken.rs[0].fields, { n: 'woke' }), JSON.stringify(woken));
  check('xread block was parked', Date.now() - started >= 200);
  check('connection fine after block', (await db.ping()) === 'PONG');

  // -- 1.1: consumer groups --------------------------------------------------------
  await db.del('jobs');
  check('xgroupCreate needs key', !!(await rejects(db.xgroupCreate('jobs', 'workers'))));
  await db.xgroupCreate('jobs', 'workers', '$', { mkStream: true });
  const busy = await rejects(db.xgroupCreate('jobs', 'workers'));
  check('xgroupCreate busygroup', busy && busy.code === 'BUSYGROUP');

  check('xgroupCreateConsumer', (await db.xgroupCreateConsumer('jobs', 'workers', 'spare'))
    && !(await db.xgroupCreateConsumer('jobs', 'workers', 'spare')));

  const j1 = await db.xadd('jobs', { task: 'a' });
  const j2 = await db.xadd('jobs', { task: 'b' });
  let got = await db.xreadGroup('workers', 'w1', 'jobs', { count: 1 });
  check('xreadGroup >', same(got, { jobs: [{ id: j1, fields: { task: 'a' } }] }), JSON.stringify(got));
  got = await db.xreadGroup('workers', 'w1', ['jobs']);
  check('xreadGroup next', same(got, { jobs: [{ id: j2, fields: { task: 'b' } }] }));
  check('xreadGroup none left', (await db.xreadGroup('workers', 'w1', 'jobs')) === null);
  check('xreadGroup block times out', (await db.xreadGroup('workers', 'w1', 'jobs', { blockMs: 100 })) === null);
  const history = await db.xreadGroup('workers', 'w1', { jobs: '0' });
  check('xreadGroup history', same(history.jobs.map((e) => e.id), [j1, j2]));
  check('xreadGroup history elsewhere empty', same(await db.xreadGroup('workers', 'w2', { jobs: '0' }), { jobs: [] }));
  const nogroup = await rejects(db.xreadGroup('nogroup', 'w1', 'jobs'));
  check('xreadGroup nogroup', nogroup && nogroup.code === 'NOGROUP');

  const summary = await db.xpending('jobs', 'workers');
  check('xpending summary', same(summary, { count: 2, min: j1, max: j2, consumers: { w1: 2 } }), JSON.stringify(summary));

  // Two deliveries each: the history re-read above counts as one, as it does in Redis.
  const pending = await db.xpendingRange('jobs', 'workers');
  check('xpendingRange', same(pending.map((p) => p.id), [j1, j2])
    && pending[0].consumer === 'w1' && pending[0].deliveries === 2, JSON.stringify(pending));
  check('xpendingRange consumer', same(await db.xpendingRange('jobs', 'workers', { consumer: 'w2' }), []));

  check('xack', (await db.xack('jobs', 'workers', j1, '9-9')) === 1);

  await sleep(60);
  check('xpendingRange idle', (await db.xpendingRange('jobs', 'workers', { idleMs: 50 })).length === 1);
  check('xclaim too fresh', same(await db.xclaim('jobs', 'workers', 'w2', 60000, [j2]), []));
  const claimed = await db.xclaim('jobs', 'workers', 'w2', 50, [j2]);
  check('xclaim', same(claimed, [{ id: j2, fields: { task: 'b' } }]), JSON.stringify(claimed));
  check('xclaim moved owner', same((await db.xpending('jobs', 'workers')).consumers, { w2: 1 }));
  check('xclaim justId', same(await db.xclaim('jobs', 'workers', 'w3', 0, [j2], { justId: true }), [j2]));
  await db.xclaim('jobs', 'workers', 'w3', 0, [j2], { idleMs: 5000, retryCount: 7, justId: true });
  const row = (await db.xpendingRange('jobs', 'workers'))[0];
  check('xclaim idle/retryCount', row.deliveries === 7 && row.idleMs >= 5000, JSON.stringify(row));
  check('xclaim force', same(await db.xclaim('jobs', 'workers', 'w3', 0, [j1], { force: true, justId: true }), [j1]));

  const j3 = await db.xadd('jobs', { task: 'c' });
  await db.xreadGroup('workers', 'w1', 'jobs');
  await sleep(60);
  let auto = await db.xautoClaim('jobs', 'workers', 'w4', 50, '0-0', { count: 1 });
  check('xautoClaim page', auto.next !== '0-0' && same(auto.claimed, [{ id: j1, fields: { task: 'a' } }])
    && same(auto.deleted, []), JSON.stringify(auto));
  auto = await db.xautoClaim('jobs', 'workers', 'w4', 0, auto.next, { justId: true });
  check('xautoClaim justId completes', auto.next === '0-0' && same(auto.claimed, [j2, j3]), JSON.stringify(auto));

  const groups = await db.xinfoGroups('jobs');
  check('xinfoGroups', groups[0].name === 'workers' && groups[0].pending === 3
    && groups[0]['last-delivered-id'] === j3, JSON.stringify(groups));
  const consumers = Object.fromEntries((await db.xinfoConsumers('jobs', 'workers')).map((c) => [c.name, c]));
  check('xinfoConsumers', consumers.w4.pending === 3 && consumers.spare.pending === 0
    && typeof consumers.w4.idle === 'number', JSON.stringify(consumers));
  const stream = await db.xinfoStream('jobs');
  check('xinfoStream', stream.length === 3 && stream.groups === 1
    && same(stream['first-entry'], { id: j1, fields: { task: 'a' } }) && stream['last-entry'].id === j3,
    JSON.stringify(stream));

  check('xgroupDelConsumer', (await db.xgroupDelConsumer('jobs', 'workers', 'w4')) === 3);
  await db.xgroupSetId('jobs', 'workers', '0');
  const replay = await db.xreadGroup('workers', 'w5', 'jobs');
  check('xgroupSetId replays', same(replay.jobs.map((e) => e.id), [j1, j2, j3]));
  await db.xgroupSetId('jobs', 'workers');
  check('xgroupSetId $', (await db.xreadGroup('workers', 'w5', 'jobs')) === null);

  await db.xgroupCreate('jobs', 'fire', '0');
  check('xreadGroup noAck', (await db.xreadGroup('fire', 'f', 'jobs', { noAck: true })).jobs.length === 3
    && same(await db.xpending('jobs', 'fire'), { count: 0, min: null, max: null, consumers: {} }));

  // An entry trimmed while pending: history shows it with no fields, autoclaim reports it deleted.
  await db.del('trim');
  await db.xgroupCreate('trim', 'g', '$', { mkStream: true });
  const t1 = await db.xadd('trim', { n: '1' });
  await db.xreadGroup('g', 'c', 'trim');
  for (let i = 0; i < 3; i++) await db.xadd('trim', { n: String(i) }, { maxLen: 2 });
  check('trimmed entry in history', same(await db.xreadGroup('g', 'c', { trim: '0' }), { trim: [{ id: t1, fields: null }] }));
  check('xautoClaim reports deleted', same(await db.xautoClaim('trim', 'g', 'c2', 0), { next: '0-0', claimed: [], deleted: [t1] }));

  // Woken from a second connection while parked in XREADGROUP.
  addLater('jobs');
  const wokenGroup = await db.xreadGroup('workers', 'w5', 'jobs', { blockMs: 5000 });
  check('xreadGroup block woken by xadd', wokenGroup !== null && same(wokenGroup.jobs[0].fields, { n: 'woke' }),
    JSON.stringify(wokenGroup));

  check('xgroupDestroy', (await db.xgroupDestroy('jobs', 'workers')) && !(await db.xgroupDestroy('jobs', 'workers')));

  await other.close();
  await db.close();

  console.log();
  console.log(failures.length === 0 ? 'ALL PASS' : `${failures.length} FAILURE(S): ${failures.join(', ')}`);
  process.exit(failures.length === 0 ? 0 : 1);
}

main().catch((error) => {
  console.error('test run failed:', error);
  process.exit(1);
});
