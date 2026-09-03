"""MemSharp client for Python.

MemSharp speaks RESP2, so this is a thin, dependency-free client over a socket.

    from memsharp import MemSharpClient

    with MemSharpClient(port=6380) as db:
        db.set("symbol:BTC", "68350.25")
        db.zadd("book:BTC:bids", {"bid-1": 68350.25})
        top = db.zrange("book:BTC:bids", 0, 9, desc=True, withscores=True)

Start a server with `memsharp serve --port 6380`.
"""

from ._resp import ConnectionClosedError, MemSharpError, WrongTypeError
from .client import MemSharpClient

__all__ = ["MemSharpClient", "MemSharpError", "WrongTypeError", "ConnectionClosedError"]
__version__ = "1.0.0"
