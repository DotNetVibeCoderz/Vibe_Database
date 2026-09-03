"""A synchronous MemSharp client."""

from __future__ import annotations

import socket
from typing import Any, Iterable, Iterator, Mapping, Sequence

from ._resp import (
    ConnectionClosedError,
    Decoder,
    MemSharpError,
    WrongTypeError,
    encode_command,
    flatten,
)

__all__ = ["MemSharpClient", "MemSharpError", "WrongTypeError", "ConnectionClosedError"]


class MemSharpClient:
    """A connection to a MemSharp server.

    One socket, used serially. RESP replies arrive in request order, so two threads issuing commands
    on one client would each be able to read the other's reply - give each thread its own client
    instead.

    >>> with MemSharpClient() as db:
    ...     db.set("symbol:BTC", "68350.25")
    ...     db.get("symbol:BTC")
    '68350.25'
    """

    def __init__(self, host: str = "127.0.0.1", port: int = 6380, timeout: float | None = 10.0) -> None:
        self._host = host
        self._port = port
        self._timeout = timeout
        self._socket: socket.socket | None = None
        self._decoder = Decoder()

    # -- connection ------------------------------------------------------------------------

    def connect(self) -> "MemSharpClient":
        """Open the connection. Called automatically on first use."""
        if self._socket is not None:
            return self

        sock = socket.create_connection((self._host, self._port), timeout=self._timeout)

        # The workload is many small request/response round-trips, which is exactly what Nagle's
        # coalescing delay ruins.
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self._socket = sock
        return self

    def close(self) -> None:
        """Close the connection. Safe to call more than once."""
        if self._socket is not None:
            try:
                self._socket.close()
            finally:
                self._socket = None

    def __enter__(self) -> "MemSharpClient":
        return self.connect()

    def __exit__(self, *_: object) -> None:
        self.close()

    # -- command plumbing ------------------------------------------------------------------

    def execute(self, *args: Any) -> Any:
        """Send one command and return its reply. Error replies are raised."""
        self.connect()
        assert self._socket is not None

        self._socket.sendall(encode_command(*args))
        return self._read_reply()

    def pipeline(self, commands: Sequence[Sequence[Any]]) -> list[Any]:
        """Send several commands in one write and collect every reply.

        One round-trip for the whole batch instead of one each. Over a real network that is the
        single largest thing you can do for throughput.

        Errors are returned in place rather than raised, so one failing command does not hide the
        replies to the others.
        """
        if not commands:
            return []

        self.connect()
        assert self._socket is not None

        self._socket.sendall(b"".join(encode_command(*command) for command in commands))
        return [self._read_reply(raise_errors=False) for _ in commands]

    def _read_reply(self, raise_errors: bool = True) -> Any:
        assert self._socket is not None

        while True:
            ready, value = self._decoder.take()
            if ready:
                if raise_errors and isinstance(value, MemSharpError):
                    raise value
                return value

            chunk = self._socket.recv(65536)
            if not chunk:
                raise ConnectionClosedError()
            self._decoder.feed(chunk)

    # -- keys ------------------------------------------------------------------------------

    def ping(self) -> str:
        """Round-trip the server."""
        return self.execute("PING")

    def set(self, key: str, value: Any, ex: float | None = None, nx: bool = False) -> bool:
        """Store a string. ``ex`` is a lifetime in seconds; ``nx`` only writes if absent."""
        args: list[Any] = ["SET", key, value]
        if ex is not None:
            args += ["PX", int(ex * 1000)]
        if nx:
            args.append("NX")
        return self.execute(*args) is not None

    def get(self, key: str) -> str | None:
        """Read a string, or ``None`` if the key is absent."""
        return self.execute("GET", key)

    def mget(self, *keys: str) -> list[str | None]:
        """Read several strings. Missing keys come back as ``None`` in position."""
        return self.execute("MGET", *keys)

    def mset(self, mapping: Mapping[str, Any]) -> None:
        """Write several key/value pairs."""
        self.execute("MSET", *flatten(mapping.items()))

    def delete(self, *keys: str) -> int:
        """Remove keys. Returns how many existed."""
        return self.execute("DEL", *keys)

    def exists(self, *keys: str) -> int:
        """Count how many of the given keys exist."""
        return self.execute("EXISTS", *keys)

    def type(self, key: str) -> str:
        """The kind of value a key holds, or ``none``."""
        return self.execute("TYPE", key)

    def incr(self, key: str, amount: int = 1) -> int:
        """Atomically add to an integer, treating a missing key as 0."""
        return self.execute("INCRBY", key, amount)

    def incrbyfloat(self, key: str, amount: float) -> float:
        """Atomically add to a floating-point value."""
        return float(self.execute("INCRBYFLOAT", key, amount))

    def expire(self, key: str, seconds: float) -> bool:
        """Set a lifetime. Returns False if the key is absent."""
        return bool(self.execute("PEXPIRE", key, int(seconds * 1000)))

    def ttl(self, key: str) -> float | None:
        """Remaining lifetime in seconds, or ``None`` if the key is permanent or absent."""
        milliseconds = self.execute("PTTL", key)
        return None if milliseconds < 0 else milliseconds / 1000.0

    def persist(self, key: str) -> bool:
        """Clear a key's expiry."""
        return bool(self.execute("PERSIST", key))

    def keys(self, pattern: str = "*") -> list[str]:
        """Every key matching a glob. Prefer :meth:`scan` on a large keyspace."""
        return self.execute("KEYS", pattern)

    def scan(self, pattern: str = "*", count: int = 500) -> Iterator[str]:
        """Iterate the keyspace a page at a time.

        The loop runs until the server returns a zero cursor, which is the same contract Redis uses.
        """
        cursor = 0
        while True:
            cursor_text, page = self.execute("SCAN", cursor, "MATCH", pattern, "COUNT", count)
            yield from page
            cursor = int(cursor_text)
            if cursor == 0:
                return

    # -- lists -----------------------------------------------------------------------------

    def lpush(self, key: str, *values: Any) -> int:
        """Push onto the head of a list. Returns the new length."""
        return self.execute("LPUSH", key, *values)

    def rpush(self, key: str, *values: Any) -> int:
        """Push onto the tail of a list. Returns the new length."""
        return self.execute("RPUSH", key, *values)

    def lpop(self, key: str) -> str | None:
        """Remove and return the head."""
        return self.execute("LPOP", key)

    def rpop(self, key: str) -> str | None:
        """Remove and return the tail."""
        return self.execute("RPOP", key)

    def lrange(self, key: str, start: int = 0, stop: int = -1) -> list[str]:
        """Elements in an inclusive index range. Negative indices count back from the end."""
        return self.execute("LRANGE", key, start, stop)

    def llen(self, key: str) -> int:
        """Number of elements in a list."""
        return self.execute("LLEN", key)

    def ltrim(self, key: str, start: int, stop: int) -> None:
        """Keep only an index range - the way to cap a feed at a fixed length."""
        self.execute("LTRIM", key, start, stop)

    # -- hashes ----------------------------------------------------------------------------

    def hset(self, key: str, field: str | None = None, value: Any = None, mapping: Mapping[str, Any] | None = None) -> int:
        """Set one field, or several with ``mapping``. Returns how many fields were new."""
        if mapping:
            return self.execute("HSET", key, *flatten(mapping.items()))
        if field is None:
            raise ValueError("hset needs either field and value, or mapping")
        return self.execute("HSET", key, field, value)

    def hget(self, key: str, field: str) -> str | None:
        """Read one field."""
        return self.execute("HGET", key, field)

    def hgetall(self, key: str) -> dict[str, str]:
        """Every field and value. The server sends them flattened; this pairs them back up."""
        flat = self.execute("HGETALL", key)
        return dict(zip(flat[::2], flat[1::2]))

    def hdel(self, key: str, *fields: str) -> int:
        """Remove fields. Returns how many existed."""
        return self.execute("HDEL", key, *fields)

    def hincrby(self, key: str, field: str, amount: int = 1) -> int:
        """Atomically add to an integer field."""
        return self.execute("HINCRBY", key, field, amount)

    # -- sets ------------------------------------------------------------------------------

    def sadd(self, key: str, *members: Any) -> int:
        """Add members. Returns how many were new."""
        return self.execute("SADD", key, *members)

    def srem(self, key: str, *members: Any) -> int:
        """Remove members. Returns how many were present."""
        return self.execute("SREM", key, *members)

    def smembers(self, key: str) -> set[str]:
        """Every member of a set."""
        return set(self.execute("SMEMBERS", key))

    def sismember(self, key: str, member: Any) -> bool:
        """True if a set contains a member."""
        return bool(self.execute("SISMEMBER", key, member))

    # -- sorted sets -----------------------------------------------------------------------

    def zadd(self, key: str, mapping: Mapping[str, float]) -> int:
        """Add scored members. Returns how many were new."""
        args: list[Any] = []
        for member, score in mapping.items():
            args += [score, member]
        return self.execute("ZADD", key, *args)

    def zrange(self, key: str, start: int = 0, stop: int = -1, desc: bool = False, withscores: bool = False) -> list[Any]:
        """Members in a rank range."""
        command = "ZREVRANGE" if desc else "ZRANGE"
        args: list[Any] = [command, key, start, stop]
        if withscores:
            args.append("WITHSCORES")

        reply = self.execute(*args)
        if not withscores:
            return reply
        return [(member, float(score)) for member, score in zip(reply[::2], reply[1::2])]

    def zrangebyscore(self, key: str, low: float, high: float, withscores: bool = False) -> list[Any]:
        """Members whose score falls in an inclusive range."""
        args: list[Any] = ["ZRANGEBYSCORE", key, low, high]
        if withscores:
            args.append("WITHSCORES")

        reply = self.execute(*args)
        if not withscores:
            return reply
        return [(member, float(score)) for member, score in zip(reply[::2], reply[1::2])]

    def zscore(self, key: str, member: str) -> float | None:
        """A member's score."""
        score = self.execute("ZSCORE", key, member)
        return None if score is None else float(score)

    def zcard(self, key: str) -> int:
        """Number of members."""
        return self.execute("ZCARD", key)

    # -- streams ---------------------------------------------------------------------------

    def xadd(self, key: str, fields: Mapping[str, Any], maxlen: int | None = None) -> str:
        """Append an entry. Returns the assigned ``ms-seq`` id."""
        args: list[Any] = ["XADD", key]
        if maxlen is not None:
            args += ["MAXLEN", maxlen]
        args.append("*")
        args += flatten(fields.items())
        return self.execute(*args)

    def xrange(self, key: str, start: str = "-", end: str = "+", count: int | None = None) -> list[tuple[str, dict[str, str]]]:
        """Entries in an id range, oldest first."""
        args: list[Any] = ["XRANGE", key, start, end]
        if count is not None:
            args += ["COUNT", count]

        return [(entry_id, dict(zip(flat[::2], flat[1::2]))) for entry_id, flat in self.execute(*args)]

    def xlen(self, key: str) -> int:
        """Number of entries."""
        return self.execute("XLEN", key)

    # -- time series -----------------------------------------------------------------------

    def ts_create(self, key: str, retention: int = 0) -> None:
        """Create a time series, optionally capped at ``retention`` samples."""
        self.execute("TS.CREATE", key, "RETENTION", retention)

    def ts_add(self, key: str, value: float, timestamp: int | None = None) -> int:
        """Append a sample. Returns the timestamp written."""
        return self.execute("TS.ADD", key, "*" if timestamp is None else timestamp, value)

    def ts_range(self, key: str, start: int | str = "-", end: int | str = "+") -> list[tuple[int, float]]:
        """Samples in an inclusive timestamp range."""
        return [(int(ts), float(value)) for ts, value in self.execute("TS.RANGE", key, start, end)]

    def ts_aggregate(self, key: str, start: int, end: int, bucket_ms: int, how: str = "avg") -> list[tuple[int, float]]:
        """Fold a range into fixed-width buckets.

        ``how`` is one of avg, min, max, sum, count, first, last.
        """
        rows = self.execute("TS.AGGREGATE", key, start, end, bucket_ms, how)
        return [(int(ts), float(value)) for ts, value in rows]

    # -- pub/sub ---------------------------------------------------------------------------

    def publish(self, channel: str, message: Any) -> int:
        """Publish a message. Returns how many subscribers received it."""
        return self.execute("PUBLISH", channel, message)

    def subscribe(self, *channels: str) -> Iterator[tuple[str, str]]:
        """Subscribe and yield ``(channel, message)`` until the connection closes.

        This takes over the connection: the server may push a message between any request and its
        reply, so a subscribed client cannot also run ordinary commands. Use a second client.
        """
        self.connect()
        assert self._socket is not None

        self._socket.sendall(encode_command("SUBSCRIBE", *channels))

        while True:
            try:
                reply = self._read_reply(raise_errors=False)
            except (ConnectionClosedError, OSError):
                return

            # Every push is an array whose head names the kind. The subscribe acknowledgement has
            # the same shape, so it is filtered out here rather than surfacing as a message.
            if isinstance(reply, list) and len(reply) >= 3:
                if reply[0] == "message":
                    yield reply[1], reply[2]
                elif reply[0] == "pmessage" and len(reply) >= 4:
                    yield reply[2], reply[3]

    # -- query and server ------------------------------------------------------------------

    def sql(self, query: str) -> list[dict[str, str | None]]:
        """Run a SELECT against the keyspace and return rows as dicts.

        The server replies with the column names as the first row; this pairs them with each row so
        callers work with names rather than positions.
        """
        reply = self.execute("SQL", query)
        if isinstance(reply, int):
            return []                       # a DELETE - reply is the affected count
        if not reply:
            return []

        columns, *rows = reply
        return [dict(zip(columns, row)) for row in rows]

    def sql_delete(self, query: str) -> int:
        """Run a DELETE against the keyspace and return the number of rows removed."""
        reply = self.execute("SQL", query)
        return reply if isinstance(reply, int) else 0

    def info(self) -> dict[str, str]:
        """Server statistics, parsed into a dict. Section headings are dropped."""
        result: dict[str, str] = {}
        for line in self.execute("INFO").splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            name, _, value = line.partition(":")
            result[name] = value
        return result

    def dbsize(self) -> int:
        """Number of keys in the database."""
        return self.execute("DBSIZE")

    def flushdb(self) -> None:
        """Remove every key."""
        self.execute("FLUSHDB")

    def save(self) -> None:
        """Write a snapshot synchronously."""
        self.execute("SAVE")
