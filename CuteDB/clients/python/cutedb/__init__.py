"""CuteDB client for Python.

Talks to ``cutedb-server`` over HTTP. Built by Gravicode Studios, led by Kang Fadhil.

CuteDB is an embedded database, so this client is a client for the *server* that wraps one — not
a binding to the engine. That is a deliberate trade: one HTTP endpoint is a far smaller surface to
keep correct across three languages and six platforms than three sets of FFI bindings would be,
and the network hop is irrelevant next to the work most calls do.

Nothing here is imported from outside the standard library.

    from cutedb import CuteClient

    with CuteClient("http://127.0.0.1:8420") as db:
        orders = db.collection("orders")
        orders.insert({"customer": "Sari", "total": 249000})

        result = db.query(
            "SELECT address.city AS city, SUM(total) AS revenue "
            "FROM orders WHERE status = @status GROUP BY address.city",
            {"status": "selesai"},
        )
        for row in result.rows:
            print(row["city"], row["revenue"])
"""

from __future__ import annotations

import json
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field
from decimal import Decimal
from typing import Any, Iterable, Iterator, Mapping, Sequence

__all__ = [
    "CuteClient",
    "CuteCollection",
    "CuteError",
    "CuteQueryError",
    "QueryResult",
    "IndexInfo",
    "__version__",
]

__version__ = "2.0.0"

Document = dict[str, Any]


class CuteError(Exception):
    """Anything the server refused, or any transport failure."""

    def __init__(self, message: str, *, code: str | None = None, status: int | None = None) -> None:
        super().__init__(message)
        self.code = code
        self.status = status


class CuteQueryError(CuteError):
    """CuteQL that would not parse or would not run.

    The message carries the server's caret line pointing at the offending character, so printing
    it directly is usually the most useful thing to do.
    """


@dataclass(frozen=True)
class IndexInfo:
    """One secondary index."""

    name: str
    path: str
    unique: bool = False
    keys: int = 0
    entries: int = 0


@dataclass(frozen=True)
class QueryResult:
    """What a statement produced."""

    kind: str
    columns: list[str]
    rows: list[Document]
    affected: int
    duration_ms: float
    plan: str = ""

    def __iter__(self) -> Iterator[Document]:
        return iter(self.rows)

    def __len__(self) -> int:
        return len(self.rows)

    def __getitem__(self, index: int) -> Document:
        return self.rows[index]

    def scalar(self) -> Any:
        """The single value of a one-row, one-column result — what an aggregate returns."""
        if not self.rows or not self.columns:
            return None
        return self.rows[0].get(self.columns[0])

    @classmethod
    def _from_json(cls, payload: Mapping[str, Any]) -> "QueryResult":
        return cls(
            kind=payload.get("kind", "select"),
            columns=list(payload.get("columns", [])),
            rows=list(payload.get("rows", [])),
            affected=int(payload.get("affected", 0)),
            duration_ms=float(payload.get("durationMs", 0.0)),
            plan=payload.get("plan", ""),
        )


class _DecimalEncoder(json.JSONEncoder):
    """Writes ``Decimal`` as a JSON number rather than refusing it.

    A retail database is full of money, and ``json`` will not encode ``Decimal`` at all by default.
    Emitting the exact digits — rather than going through ``float`` — is what keeps a total that
    was exact in Python exact in the database.
    """

    def default(self, o: Any) -> Any:
        if isinstance(o, Decimal):
            return _RawJson(format(o, "f"))
        return super().default(o)


class _RawJson(float):
    """A float subclass whose repr is the exact decimal text.

    ``json`` formats floats with ``repr``, so overriding it is the supported way to emit a number
    that is not a Python float without post-processing the encoded string.
    """

    def __new__(cls, text: str) -> "_RawJson":
        instance = super().__new__(cls, float(text))
        instance._text = text  # type: ignore[attr-defined]
        return instance

    def __repr__(self) -> str:
        return self._text  # type: ignore[attr-defined]


class CuteCollection:
    """One collection on the server."""

    def __init__(self, client: "CuteClient", name: str) -> None:
        self._client = client
        self.name = name

    def __repr__(self) -> str:
        return f"CuteCollection({self.name!r})"

    # -- writing ----------------------------------------------------------------------------

    def insert(self, document: Document) -> Document:
        """Insert one document and return it, with the id the server assigned."""
        return self._client._request(
            "POST", f"/v1/collections/{self.name}/documents", body=document
        )

    def insert_many(self, documents: Iterable[Document]) -> list[str]:
        """Insert many documents in one request and return their ids.

        Far faster than a loop over :meth:`insert`: the server applies the whole batch under a
        single lock and flushes once, rather than once per document.
        """
        payload = list(documents)
        if not payload:
            return []

        response = self._client._request(
            "POST", f"/v1/collections/{self.name}/documents", body=payload
        )
        return list(response.get("ids", []))

    def replace(self, document_id: str, document: Document) -> Document:
        """Replace a document wholesale."""
        return self._client._request(
            "PUT", f"/v1/collections/{self.name}/documents/{document_id}", body=document
        )

    def patch(self, document_id: str, changes: Document) -> Document:
        """Merge fields into a document.

        A key containing a dot is a path, so ``{"address.city": "Bandung"}`` reaches into the
        subdocument while ``{"address": {...}}`` replaces it.
        """
        return self._client._request(
            "PATCH", f"/v1/collections/{self.name}/documents/{document_id}", body=changes
        )

    def delete(self, document_id: str) -> bool:
        """Delete a document. Returns False when it was not there."""
        try:
            self._client._request("DELETE", f"/v1/collections/{self.name}/documents/{document_id}")
            return True
        except CuteError as error:
            if error.status == 404:
                return False
            raise

    # -- reading ----------------------------------------------------------------------------

    def get(self, document_id: str) -> Document | None:
        """Fetch a document by id, or None."""
        try:
            return self._client._request(
                "GET", f"/v1/collections/{self.name}/documents/{document_id}"
            )
        except CuteError as error:
            if error.status == 404:
                return None
            raise

    def find(
        self,
        filter: str | None = None,
        *,
        limit: int = 100,
        offset: int = 0,
    ) -> QueryResult:
        """Page through the collection, optionally filtered by a CuteQL WHERE expression."""
        query: dict[str, Any] = {"limit": limit}
        if filter:
            query["filter"] = filter
        if offset:
            query["offset"] = offset

        payload = self._client._request(
            "GET", f"/v1/collections/{self.name}/documents", query=query
        )
        return QueryResult._from_json(payload)

    def stats(self) -> Document:
        """Size and index statistics for this collection."""
        return self._client._request("GET", f"/v1/collections/{self.name}")

    def count(self, filter: str | None = None) -> int:
        """How many documents match, or how many there are in total."""
        where = f" WHERE {filter}" if filter else ""
        result = self._client.query(f"SELECT COUNT(*) AS n FROM {self.name}{where}")
        return int(result.scalar() or 0)

    # -- indexes ----------------------------------------------------------------------------

    def create_index(self, path: str, *, name: str | None = None, unique: bool = False) -> IndexInfo:
        """Create a secondary index over a document path."""
        body: dict[str, Any] = {"path": path, "unique": unique}
        if name:
            body["name"] = name

        payload = self._client._request(
            "POST", f"/v1/collections/{self.name}/indexes", body=body
        )
        return IndexInfo(
            name=payload["name"],
            path=payload["path"],
            unique=payload.get("unique", False),
            keys=payload.get("keys", 0),
            entries=payload.get("entries", 0),
        )

    def drop_index(self, name: str) -> bool:
        """Drop an index. Returns False when there was none by that name."""
        try:
            self._client._request("DELETE", f"/v1/collections/{self.name}/indexes/{name}")
            return True
        except CuteError as error:
            if error.status == 404:
                return False
            raise

    def indexes(self) -> list[IndexInfo]:
        """The indexes on this collection."""
        payload = self.stats()
        return [
            IndexInfo(
                name=index["name"],
                path=index["path"],
                unique=index.get("unique", False),
                keys=index.get("keys", 0),
                entries=index.get("entries", 0),
            )
            for index in payload.get("indexes", [])
        ]


class CuteClient:
    """A connection to a CuteDB server.

    The client is stateless — there is no session to keep alive and no pool to manage — so one
    instance is safe to share across threads.
    """

    def __init__(
        self,
        base_url: str = "http://127.0.0.1:8420",
        *,
        api_key: str | None = None,
        timeout: float = 30.0,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.timeout = timeout

    def __enter__(self) -> "CuteClient":
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def __repr__(self) -> str:
        return f"CuteClient({self.base_url!r})"

    # -- database ---------------------------------------------------------------------------

    def health(self) -> Document:
        """Check the server is up, and which engine build it is running."""
        return self._request("GET", "/health")

    def collection(self, name: str) -> CuteCollection:
        """A handle to a collection. Created on first insert if it does not exist."""
        return CuteCollection(self, name)

    def collections(self) -> list[Document]:
        """Every collection, with its size."""
        return self._request("GET", "/v1/collections")

    def drop_collection(self, name: str) -> bool:
        """Drop a collection and everything in it."""
        try:
            self._request("DELETE", f"/v1/collections/{name}")
            return True
        except CuteError as error:
            if error.status == 404:
                return False
            raise

    def query(self, query: str, parameters: Mapping[str, Any] | None = None) -> QueryResult:
        """Run a CuteQL statement.

        Bind values through ``parameters`` rather than building the statement by concatenation:
        a bound value is used as a value and can never be reinterpreted as syntax.
        """
        body: dict[str, Any] = {"query": query}
        if parameters:
            body["parameters"] = dict(parameters)

        return QueryResult._from_json(self._request("POST", "/v1/query", body=body))

    def explain(self, query: str, parameters: Mapping[str, Any] | None = None) -> Document:
        """Report how a SELECT would find its rows, without returning them."""
        body: dict[str, Any] = {"query": query}
        if parameters:
            body["parameters"] = dict(parameters)

        return self._request("POST", "/v1/explain", body=body)

    def stats(self) -> Document:
        """Totals across the database."""
        return self._request("GET", "/v1/stats")

    def compact(self) -> int:
        """Rewrite the file with only current state. Returns bytes reclaimed."""
        return int(self._request("POST", "/v1/compact").get("reclaimedBytes", 0))

    # -- transport --------------------------------------------------------------------------

    def _request(
        self,
        method: str,
        path: str,
        *,
        body: Any = None,
        query: Mapping[str, Any] | None = None,
    ) -> Any:
        url = self.base_url + path
        if query:
            url += "?" + urllib.parse.urlencode({k: v for k, v in query.items() if v is not None})

        data = None
        headers = {"Accept": "application/json"}

        if body is not None:
            data = json.dumps(body, cls=_DecimalEncoder, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json; charset=utf-8"

        if self.api_key:
            headers["X-API-Key"] = self.api_key

        request = urllib.request.Request(url, data=data, headers=headers, method=method)

        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                payload = response.read()
        except urllib.error.HTTPError as error:
            raise self._to_error(error) from None
        except urllib.error.URLError as error:
            raise CuteError(f"Could not reach {self.base_url}: {error.reason}") from None

        if not payload:
            return None

        return json.loads(payload.decode("utf-8"))

    @staticmethod
    def _to_error(error: urllib.error.HTTPError) -> CuteError:
        code = None
        message = error.reason or "request failed"

        try:
            payload = json.loads(error.read().decode("utf-8"))
            code = payload.get("error")
            message = payload.get("message", message)
        except (ValueError, OSError):
            # The server always sends JSON for its own failures. A body that is not JSON means
            # something else answered — a proxy, usually — and the status is all there is to go on.
            pass

        kind = CuteQueryError if code == "invalid_query" else CuteError
        return kind(message, code=code, status=error.code)
