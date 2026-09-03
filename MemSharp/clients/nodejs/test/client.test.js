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

  await db.close();

  console.log();
  console.log(failures.length === 0 ? 'ALL PASS' : `${failures.length} FAILURE(S): ${failures.join(', ')}`);
  process.exit(failures.length === 0 ? 0 : 1);
}

main().catch((error) => {
  console.error('test run failed:', error);
  process.exit(1);
});
