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

from memsharp import MemSharpClient, WrongTypeError  # noqa: E402

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

    print()
    print("ALL PASS" if not failures else f"{len(failures)} FAILURE(S): {', '.join(failures)}")
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
