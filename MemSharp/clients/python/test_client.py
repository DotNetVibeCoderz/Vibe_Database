"""Integration tests for the Python client.

These run against a live server rather than a mock, because the thing worth testing is that the
wire encoding matches what MemSharp actually sends back. Start one first:

    memsharp serve --port 6391 --quiet
    python -m pytest clients/python/test_client.py

or run this file directly to get the same checks without pytest.
"""

from __future__ import annotations

import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from memsharp import MemSharpClient, MemSharpError, WrongTypeError  # noqa: E402

PORT = int(os.environ.get("MEMSHARP_TEST_PORT", "6391"))

failures: list[str] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    status = "  ok  " if condition else " FAIL "
    print(f"{status} {name}{f'  [{detail}]' if detail else ''}")
    if not condition:
        failures.append(name)


def main() -> int:
    with MemSharpClient(port=PORT) as db:
        db.flushdb()

        # -- strings and counters ------------------------------------------------------
        check("ping", db.ping() == "PONG")
        db.set("k", "v")
        check("set/get", db.get("k") == "v")
        check("get missing", db.get("absent") is None)
        check("incr", db.incr("n", 41) == 41 and db.incr("n") == 42)
        check("incrbyfloat", abs(db.incrbyfloat("f", 1.5) - 1.5) < 1e-9)
        check("mget order", db.mget("k", "absent", "n") == ["v", None, "42"])

        db.mset({"a": "1", "b": "2"})
        check("mset", db.mget("a", "b") == ["1", "2"])
        check("exists", db.exists("a", "b", "nope") == 2)
        check("type", db.type("k") == "string")
        check("delete", db.delete("a", "b") == 2)

        # -- expiry --------------------------------------------------------------------
        db.set("temp", "v", ex=60)
        ttl = db.ttl("temp")
        check("ttl set", ttl is not None and 55 < ttl <= 60, str(ttl))
        check("ttl permanent", db.ttl("k") is None)
        check("persist", db.persist("temp") and db.ttl("temp") is None)

        db.set("brief", "v", ex=0.05)
        time.sleep(0.2)
        check("expiry evicts", db.get("brief") is None)

        check("set nx", db.set("k", "other", nx=True) is False and db.get("k") == "v")

        # -- lists ---------------------------------------------------------------------
        db.rpush("l", "a", "b", "c")
        check("rpush/lrange", db.lrange("l") == ["a", "b", "c"])
        db.lpush("l", "z")
        check("lpush", db.lrange("l", 0, 0) == ["z"])
        check("llen", db.llen("l") == 4)
        check("lpop", db.lpop("l") == "z")
        check("rpop", db.rpop("l") == "c")
        db.ltrim("l", 0, 0)
        check("ltrim", db.lrange("l") == ["a"])
        check("negative index range", db.rpush("r", "1", "2", "3") == 3 and db.lrange("r", -2, -1) == ["2", "3"])

        # -- hashes --------------------------------------------------------------------
        db.hset("h", mapping={"name": "Kang Fadhil", "desk": "Jakarta"})
        check("hset mapping", db.hgetall("h") == {"name": "Kang Fadhil", "desk": "Jakarta"})
        check("hget", db.hget("h", "desk") == "Jakarta")
        check("hincrby", db.hincrby("h", "fills", 5) == 5)
        check("hdel", db.hdel("h", "desk") == 1 and db.hget("h", "desk") is None)

        # -- sets ----------------------------------------------------------------------
        db.sadd("s", "x", "y", "x")
        check("sadd dedupes", db.smembers("s") == {"x", "y"})
        check("sismember", db.sismember("s", "x") and not db.sismember("s", "q"))
        check("srem", db.srem("s", "x") == 1)

        # -- sorted sets ---------------------------------------------------------------
        db.zadd("z", {"low": 1.0, "high": 3.0, "mid": 2.0})
        check("zadd/zrange", db.zrange("z") == ["low", "mid", "high"])
        check("zrange desc", db.zrange("z", desc=True) == ["high", "mid", "low"])
        check("zrange withscores", db.zrange("z", withscores=True)[0] == ("low", 1.0))
        check("zrangebyscore", db.zrangebyscore("z", 1.5, 2.5) == ["mid"])
        check("zscore", db.zscore("z", "high") == 3.0)
        check("zscore missing", db.zscore("z", "nope") is None)
        check("zcard", db.zcard("z") == 3)

        # -- streams -------------------------------------------------------------------
        first = db.xadd("stream", {"sym": "BTC", "qty": "5"})
        db.xadd("stream", {"sym": "ETH", "qty": "12"})
        check("xadd returns id", "-" in first, first)
        check("xlen", db.xlen("stream") == 2)

        entries = db.xrange("stream")
        check("xrange fields", entries[0][1] == {"sym": "BTC", "qty": "5"}, str(entries[0][1]))

        for i in range(50):
            db.xadd("capped", {"n": str(i)}, maxlen=10)
        check("xadd maxlen", db.xlen("capped") == 10)

        # -- time series ---------------------------------------------------------------
        db.ts_create("ts", retention=1000)
        for i in range(20):
            db.ts_add("ts", float(i), timestamp=i * 100)
        samples = db.ts_range("ts", 0, 10_000)
        check("ts range", len(samples) == 20 and samples[0] == (0, 0.0), str(samples[:2]))

        buckets = db.ts_aggregate("ts", 0, 10_000, 500, "max")
        check("ts aggregate", len(buckets) == 4 and buckets[0][1] == 4.0, str(buckets))

        # -- SQL -----------------------------------------------------------------------
        for i in range(20):
            db.set(f"order:{i}", "x" * i)

        rows = db.sql("SELECT key, size FROM keys WHERE key LIKE 'order:%' AND size > 15 ORDER BY size DESC LIMIT 3")
        check("sql rows are dicts", len(rows) == 3 and rows[0]["size"] == "19", str(rows[:1]))
        check("sql column names", set(rows[0].keys()) == {"key", "size"})

        removed = db.sql_delete("SELECT * FROM keys WHERE key = 'nonexistent'")
        check("sql select is not a delete", removed == 0)
        check("sql delete", db.sql_delete("DELETE FROM keys WHERE key LIKE 'order:1%'") == 11)

        # -- errors --------------------------------------------------------------------
        try:
            db.get("l")
            check("wrongtype raises", False)
        except WrongTypeError as error:
            check("wrongtype raises", error.code == "WRONGTYPE", error.message)

        # An error must not desynchronise the connection.
        check("connection survives an error", db.ping() == "PONG")

        # -- pipelining ----------------------------------------------------------------
        replies = db.pipeline([["SET", f"p{i}", str(i)] for i in range(500)])
        check("pipeline replies", len(replies) == 500 and all(r == "OK" for r in replies))
        check("pipeline stored", db.get("p499") == "499")

        mixed = db.pipeline([["PING"], ["NOSUCHCOMMAND"], ["PING"]])
        check("pipeline returns errors in place",
              mixed[0] == "PONG" and isinstance(mixed[2], str) and isinstance(mixed[1], Exception))

        # -- scan ----------------------------------------------------------------------
        found = list(db.scan("p*", count=64))
        check("scan pages", len(found) == 500, str(len(found)))

        # -- pub/sub -------------------------------------------------------------------
        import threading

        received: list[tuple[str, str]] = []

        def pump() -> None:
            with MemSharpClient(port=PORT) as subscriber:
                for channel, message in subscriber.subscribe("news"):
                    # The warmup publishes below exist only to detect that the subscription has
                    # registered; counting them would fill the quota before the real messages.
                    if message == "warmup":
                        continue
                    received.append((channel, message))
                    if len(received) == 2:
                        return

        thread = threading.Thread(target=pump, daemon=True)
        thread.start()

        deadline = time.time() + 5
        while db.execute("PUBLISH", "news", "warmup") == 0 and time.time() < deadline:
            time.sleep(0.05)

        db.publish("news", "one")
        db.publish("news", "two")
        thread.join(timeout=5)

        payloads = [message for _, message in received]
        check("pubsub delivers", "one" in payloads and "two" in payloads, str(payloads))

        # -- server --------------------------------------------------------------------
        info = db.info()
        check("info parses", info.get("product") == "MemSharp" and "keys" in info)
        check("dbsize", db.dbsize() > 0)

        keyspace = db.info("keyspace")
        check("info section", keyspace.get("db0", "").startswith("keys=") and "redis_version" not in keyspace,
              str(keyspace))
        two = db.info("server", "memory")
        check("info several sections", "memsharp_version" in two and "used_memory" in two)

        config = db.config_get()
        check("config_get all", config.get("maxmemory-policy") == "noeviction" and "shards" in config)
        check("config_get pattern", db.config_get("maxmemory", "nope") == {"maxmemory": "0"})
        try:
            db.execute("CONFIG", "SET", "maxmemory", "1")
            check("config set refused", False)
        except MemSharpError:
            check("config set refused", True)
        db.config_resetstat()

        check("memory_usage", (db.memory_usage("h") or 0) > 0 and db.memory_usage("absent") is None)
        check("memory_usage samples", (db.memory_usage("r", samples=2) or 0) > 0)

        # -- 1.1: strings --------------------------------------------------------------
        db.set("utf", "héllo")
        check("strlen counts bytes", db.strlen("utf") == 6)
        check("getrange", db.getrange("utf", 0, 2) == "hé" and db.getrange("utf", -3, -1) == "llo")
        check("getrange missing key", db.getrange("absent", 0, 5) == "")
        try:
            db.getrange("utf", 0, 1)
            check("getrange refuses split char", False)
        except MemSharpError as error:
            check("getrange refuses split char", "multi-byte" in error.message, error.message)
        check("setrange pads", db.setrange("sr", 3, "ab") == 5 and db.get("sr") == "\x00\x00\x00ab")
        check("setrange overwrites", db.setrange("utf", 3, "LO") == 6 and db.get("utf") == "héLOo")

        # -- 1.1: lists ----------------------------------------------------------------
        db.rpush("pos", "a", "b", "c", "b", "b")
        check("lpos", db.lpos("pos", "b") == 1 and db.lpos("pos", "q") is None)
        check("lpos rank", db.lpos("pos", "b", rank=-1) == 4 and db.lpos("pos", "b", rank=2) == 3)
        check("lpos count", db.lpos("pos", "b", count=0) == [1, 3, 4] and db.lpos("pos", "b", count=2) == [1, 3])
        check("lpos count none", db.lpos("pos", "q", count=1) == [])
        check("lpos maxlen", db.lpos("pos", "b", count=0, maxlen=3) == [1])

        # -- 1.1: set and sorted-set stores --------------------------------------------
        db.sadd("sa", "1", "2", "3")
        db.sadd("sb", "2", "3", "4")
        check("sinterstore", db.sinterstore("si", "sa", "sb") == 2 and db.smembers("si") == {"2", "3"})
        check("sunionstore", db.sunionstore("su", "sa", "sb") == 4)
        check("sdiffstore", db.sdiffstore("sd", "sa", "sb") == 1 and db.smembers("sd") == {"1"})
        check("empty store deletes", db.sinterstore("si", "sa", "absent") == 0 and db.exists("si") == 0)

        db.zadd("z1", {"x": 1, "y": 2})
        db.zadd("z2", {"y": 10, "w": 20})
        check("zunionstore", db.zunionstore("zu", ["z1", "z2"]) == 3 and db.zscore("zu", "y") == 12.0)
        check("zunionstore weights/max",
              db.zunionstore("zw", ["z1", "z2"], weights=[1, 2], aggregate="max") == 3
              and db.zrange("zw", withscores=True) == [("x", 1.0), ("y", 20.0), ("w", 40.0)])
        check("zinterstore", db.zinterstore("zi", ["z1", "z2"], aggregate="min") == 1 and db.zscore("zi", "y") == 2.0)
        db.sadd("plain", "x")
        check("zinterstore plain set scores 1",
              db.zinterstore("zp", ["z1", "plain"]) == 1 and db.zscore("zp", "x") == 2.0)

        # -- 1.1: scan cursor ----------------------------------------------------------
        # The shard index rides in the cursor's high 32 bits, so any cursor past the first shard is
        # above 2^32. Walk the raw cursors to prove one was, then that scan() survives it.
        cursors, cursor = [], 0
        while True:
            cursor_text, _ = db.execute("SCAN", cursor, "COUNT", 1)
            cursor = int(cursor_text)
            cursors.append(cursor)
            if cursor == 0:
                break
        check("scan cursor exceeds 2^32", max(cursors) > 2**32, str(max(cursors)))
        check("scan follows large cursors",
              sorted(db.scan("p49*", count=1)) == sorted(f"p49{i}" for i in [""] + list(range(10))))

        # -- 1.1: xread ----------------------------------------------------------------
        db.delete("rs", "rs2")
        a = db.xadd("rs", {"n": "1"})
        b = db.xadd("rs", {"n": "2"})
        read = db.xread({"rs": "0", "rs2": "0"})
        check("xread from 0", read == {"rs": [(a, {"n": "1"}), (b, {"n": "2"})]}, str(read))
        check("xread count", db.xread({"rs": "0"}, count=1) == {"rs": [(a, {"n": "1"})]})
        check("xread after id", db.xread({"rs": a}) == {"rs": [(b, {"n": "2"})]})
        check("xread nothing new", db.xread("rs") is None)

        started = time.time()
        check("xread block times out to None", db.xread("rs", block=0.15) is None)
        check("xread block waited", time.time() - started >= 0.1)

        def add_later(key: str) -> None:
            time.sleep(0.3)
            with MemSharpClient(port=PORT) as other:
                other.xadd(key, {"n": "woke"})

        threading.Thread(target=add_later, args=("rs",), daemon=True).start()
        started = time.time()
        woken = db.xread("rs", block=0)                    # forever: only the XADD can end it
        check("xread block woken by xadd", woken is not None and woken["rs"][0][1] == {"n": "woke"}, str(woken))
        check("xread block was parked", time.time() - started >= 0.2)
        check("connection fine after block", db.ping() == "PONG")

        # -- 1.1: consumer groups ------------------------------------------------------
        db.delete("jobs")
        try:
            db.xgroup_create("jobs", "workers")
            check("xgroup create needs key", False)
        except MemSharpError:
            check("xgroup create needs key", True)
        db.xgroup_create("jobs", "workers", mkstream=True)
        try:
            db.xgroup_create("jobs", "workers")
            check("xgroup busygroup", False)
        except MemSharpError as error:
            check("xgroup busygroup", error.code == "BUSYGROUP")

        check("xgroup createconsumer", db.xgroup_createconsumer("jobs", "workers", "spare")
              and not db.xgroup_createconsumer("jobs", "workers", "spare"))

        j1 = db.xadd("jobs", {"task": "a"})
        j2 = db.xadd("jobs", {"task": "b"})
        got = db.xreadgroup("workers", "w1", "jobs", count=1)
        check("xreadgroup >", got == {"jobs": [(j1, {"task": "a"})]}, str(got))
        got = db.xreadgroup("workers", "w1", ["jobs"])
        check("xreadgroup next", got == {"jobs": [(j2, {"task": "b"})]})
        check("xreadgroup none left", db.xreadgroup("workers", "w1", "jobs") is None)
        check("xreadgroup block times out", db.xreadgroup("workers", "w1", "jobs", block=0.1) is None)
        history = db.xreadgroup("workers", "w1", {"jobs": "0"})
        check("xreadgroup history", [e[0] for e in history["jobs"]] == [j1, j2])
        check("xreadgroup history elsewhere empty", db.xreadgroup("workers", "w2", {"jobs": "0"}) == {"jobs": []})
        try:
            db.xreadgroup("nogroup", "w1", "jobs")
            check("xreadgroup nogroup", False)
        except MemSharpError as error:
            check("xreadgroup nogroup", error.code == "NOGROUP")

        summary = db.xpending("jobs", "workers")
        check("xpending summary", summary == {"count": 2, "min": j1, "max": j2, "consumers": {"w1": 2}}, str(summary))

        # Two deliveries each: the history re-read above counts as one, as it does in Redis.
        pending = db.xpending_range("jobs", "workers")
        check("xpending range", [p["id"] for p in pending] == [j1, j2]
              and pending[0]["consumer"] == "w1" and pending[0]["deliveries"] == 2, str(pending))
        check("xpending range consumer", db.xpending_range("jobs", "workers", consumer="w2") == [])

        check("xack", db.xack("jobs", "workers", j1, "9-9") == 1)

        time.sleep(0.06)
        check("xpending range idle", len(db.xpending_range("jobs", "workers", idle=0.05)) == 1)
        check("xclaim too fresh", db.xclaim("jobs", "workers", "w2", 60, j2) == [])
        claimed = db.xclaim("jobs", "workers", "w2", 0.05, j2)
        check("xclaim", claimed == [(j2, {"task": "b"})], str(claimed))
        check("xclaim moved owner", db.xpending("jobs", "workers")["consumers"] == {"w2": 1})
        check("xclaim justid", db.xclaim("jobs", "workers", "w3", 0, j2, justid=True) == [j2])
        db.xclaim("jobs", "workers", "w3", 0, j2, idle=5, retrycount=7, justid=True)
        row = db.xpending_range("jobs", "workers")[0]
        check("xclaim idle/retrycount", row["deliveries"] == 7 and row["idle"] >= 5, str(row))
        check("xclaim force", db.xclaim("jobs", "workers", "w3", 0, j1, force=True, justid=True) == [j1])

        j3 = db.xadd("jobs", {"task": "c"})
        db.xreadgroup("workers", "w1", "jobs")
        time.sleep(0.06)
        next_start, auto, deleted = db.xautoclaim("jobs", "workers", "w4", 0.05, count=1)
        check("xautoclaim page", next_start != "0-0" and auto == [(j1, {"task": "a"})] and deleted == [],
              f"{next_start} {auto} {deleted}")
        next_start, auto, deleted = db.xautoclaim("jobs", "workers", "w4", 0, next_start, justid=True)
        check("xautoclaim justid completes", next_start == "0-0" and auto == [j2, j3], f"{next_start} {auto}")

        groups = db.xinfo_groups("jobs")
        check("xinfo groups", groups[0]["name"] == "workers" and groups[0]["pending"] == 3
              and groups[0]["last-delivered-id"] == j3, str(groups))
        consumers = {c["name"]: c for c in db.xinfo_consumers("jobs", "workers")}
        check("xinfo consumers", consumers["w4"]["pending"] == 3 and consumers["spare"]["pending"] == 0
              and isinstance(consumers["w4"]["idle"], float), str(consumers))
        stream = db.xinfo_stream("jobs")
        check("xinfo stream", stream["length"] == 3 and stream["groups"] == 1
              and stream["first-entry"] == (j1, {"task": "a"}) and stream["last-entry"][0] == j3, str(stream))

        check("xgroup delconsumer", db.xgroup_delconsumer("jobs", "workers", "w4") == 3)
        db.xgroup_setid("jobs", "workers", "0")
        replay = db.xreadgroup("workers", "w5", "jobs")
        check("xgroup setid replays", [e[0] for e in replay["jobs"]] == [j1, j2, j3])
        db.xgroup_setid("jobs", "workers")
        check("xgroup setid $", db.xreadgroup("workers", "w5", "jobs") is None)

        db.xgroup_create("jobs", "fire", "0")
        check("xreadgroup noack", len(db.xreadgroup("fire", "f", "jobs", noack=True)["jobs"]) == 3
              and db.xpending("jobs", "fire") == {"count": 0, "min": None, "max": None, "consumers": {}})

        # An entry trimmed while pending: history shows it with no fields, autoclaim reports it deleted.
        db.delete("trim")
        db.xgroup_create("trim", "g", mkstream=True)
        t1 = db.xadd("trim", {"n": "1"})
        db.xreadgroup("g", "c", "trim")
        for i in range(3):
            db.xadd("trim", {"n": str(i)}, maxlen=2)
        check("trimmed entry in history", db.xreadgroup("g", "c", {"trim": "0"}) == {"trim": [(t1, None)]})
        check("xautoclaim reports deleted", db.xautoclaim("trim", "g", "c2", 0) == ("0-0", [], [t1]))

        # Woken from a second connection while parked in XREADGROUP.
        threading.Thread(target=add_later, args=("jobs",), daemon=True).start()
        woken = db.xreadgroup("workers", "w5", "jobs", block=5)
        check("xreadgroup block woken by xadd", woken is not None and woken["jobs"][0][1] == {"n": "woke"}, str(woken))

        check("xgroup destroy", db.xgroup_destroy("jobs", "workers") and not db.xgroup_destroy("jobs", "workers"))

    print()
    print("ALL PASS" if not failures else f"{len(failures)} FAILURE(S): {', '.join(failures)}")
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
