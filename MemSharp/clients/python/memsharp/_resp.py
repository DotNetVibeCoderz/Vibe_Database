"""RESP2 encoding and decoding.

MemSharp speaks the same wire protocol Redis does, so this module is deliberately small and
generic: encode a command as an array of bulk strings, decode a reply. Nothing here knows what a
MemSharp command means.
"""

from __future__ import annotations

from typing import Any, Iterable

CRLF = b"\r\n"


class MemSharpError(Exception):
    """An error reply from the server.

    ``code`` is the leading token, e.g. ``WRONGTYPE``. It is part of the wire contract, so branching
    on it is safe in a way that matching on the message text is not.
    """

    def __init__(self, code: str, message: str) -> None:
        super().__init__(f"{code} {message}")
        self.code = code
        self.message = message


class WrongTypeError(MemSharpError):
    """A command was applied to a key holding a different type."""


class ConnectionClosedError(MemSharpError):
    """The connection closed before a complete reply arrived."""

    def __init__(self, message: str = "connection closed by the server") -> None:
        super().__init__("CONN", message)


def encode_command(*args: Any) -> bytes:
    """Encode a command as a RESP array of bulk strings.

    Every argument is stringified and UTF-8 encoded, because RESP has no types on the request side -
    a bulk string is all there is. ``bool`` is special-cased to ``1``/``0`` rather than
    ``True``/``False``, which is what the server's integer parsers expect.
    """
    parts = [b"*", str(len(args)).encode(), CRLF]

    for arg in args:
        if isinstance(arg, bytes):
            payload = arg
        elif isinstance(arg, bool):
            payload = b"1" if arg else b"0"
        elif isinstance(arg, float):
            # repr round-trips; str() would truncate at 12 significant digits and silently change
            # scores on the way to a sorted set.
            payload = repr(arg).encode()
        else:
            payload = str(arg).encode()

        parts += [b"$", str(len(payload)).encode(), CRLF, payload, CRLF]

    return b"".join(parts)


class Decoder:
    """An incremental RESP reply decoder.

    Feed it bytes as they arrive and ask for replies. It buffers whatever it cannot yet parse, so a
    reply split across TCP segments is handled without the caller knowing, and a pipelined batch
    yields several replies from one read.
    """

    def __init__(self) -> None:
        self._buffer = bytearray()

    def feed(self, data: bytes) -> None:
        """Add bytes received from the socket."""
        self._buffer.extend(data)

    def take(self) -> tuple[bool, Any]:
        """Take one reply.

        Returns ``(True, value)`` when a complete reply was available, and ``(False, None)`` when
        more bytes are needed.
        """
        parsed, value, consumed = _parse(self._buffer, 0)
        if not parsed:
            return False, None
        del self._buffer[:consumed]
        return True, value


def _parse(buffer: bytearray, start: int) -> tuple[bool, Any, int]:
    """Parse one value at ``start``. Returns ``(parsed, value, end_offset)``."""
    if start >= len(buffer):
        return False, None, start

    marker = buffer[start]
    line_end = buffer.find(CRLF, start)
    if line_end < 0:
        return False, None, start

    line = bytes(buffer[start + 1 : line_end])
    after_line = line_end + 2

    if marker == ord("+"):
        return True, line.decode(), after_line

    if marker == ord("-"):
        text = line.decode()
        code, _, message = text.partition(" ")
        error = WrongTypeError(code, message) if code == "WRONGTYPE" else MemSharpError(code, message)
        return True, error, after_line

    if marker == ord(":"):
        return True, int(line), after_line

    if marker == ord("$"):
        length = int(line)
        if length < 0:
            return True, None, after_line

        end = after_line + length
        if len(buffer) < end + 2:
            return False, None, start
        return True, bytes(buffer[after_line:end]).decode(), end + 2

    if marker == ord("*"):
        count = int(line)
        if count < 0:
            return True, None, after_line

        items: list[Any] = []
        offset = after_line
        for _ in range(count):
            parsed, item, offset = _parse(buffer, offset)
            if not parsed:
                # A partial element means the whole array is partial: rewind to where the array
                # started so the next attempt re-parses it from the top.
                return False, None, start
            items.append(item)
        return True, items, offset

    raise MemSharpError("ERR", f"unknown RESP type marker {chr(marker)!r}")


def flatten(pairs: Iterable[tuple[Any, Any]]) -> list[Any]:
    """Flatten ``(a, b)`` pairs into ``[a, b, a, b, ...]`` for commands that take them."""
    result: list[Any] = []
    for first, second in pairs:
        result.append(first)
        result.append(second)
    return result
