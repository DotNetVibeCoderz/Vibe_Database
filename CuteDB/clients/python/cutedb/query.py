"""A query builder that renders CuteQL with every value bound as a parameter.

Concatenating a filter by hand is how injection bugs are written. This builds the statement from
parts instead, asks it what CuteQL it becomes, and runs it:

    from cutedb import field

    q = (orders.query()
         .where(field("address.city") == "Bandung", field("total") > 500_000)
         .select("code", "total", buyer="customer.name")
         .order_by("total", desc=True)
         .limit(10))

    q.to_cuteql()   # SELECT code, total, customer.name AS buyer FROM orders
                    #   WHERE address.city = @p0 AND total > @p1 ORDER BY total DESC LIMIT 10
    q.parameters    # {'p0': 'Bandung', 'p1': 500000}
    q.run()

Two rules make it safe, and they are the whole design:

* **Values never enter the text.** Every value becomes ``@p0``, ``@p1``… and travels in the
  request's ``parameters``, which the server binds as values. There is no escaping to get wrong
  because nothing is escaped.
* **Names are checked against the lexer.** A field path, alias or collection name *is* part of the
  text, so it must lex as exactly one identifier: ASCII letters, digits and ``_``, segments joined by
  ``.``, with ``[n]`` or ``[]`` after a segment. Anything else is refused rather than quoted —
  CuteQL has no quoted identifiers, so there is nothing safe to quote it into.

The rendering is shared with the Go and Node builders and pinned by
``clients/querybuilder-golden.json``, which the .NET test suite parses and runs.
"""

from __future__ import annotations

import math
import re
from datetime import datetime, timezone
from decimal import Decimal
from typing import TYPE_CHECKING, Any, Iterable, Mapping

if TYPE_CHECKING:  # pragma: no cover
    from . import CuteClient, QueryResult

__all__ = [
    "Query",
    "Field",
    "Condition",
    "field",
    "and_",
    "or_",
    "not_",
    "escape_like",
]

# Copied from CuteLexer.Keywords. A single bare word on this list lexes as a keyword rather than an
# identifier, so `count` or `order` cannot name a field — but `customer.count` can, because a word
# containing '.' or '[' is always an identifier.
_KEYWORDS = frozenset(
    """
    SELECT FROM WHERE ORDER BY ASC DESC LIMIT OFFSET GROUP HAVING AS AND OR NOT IN LIKE BETWEEN IS
    NULL TRUE FALSE MISSING DELETE UPDATE SET INSERT INTO VALUES DISTINCT COUNT SUM AVG MIN MAX
    EXISTS
    """.split()
)

# ASCII only, deliberately. The lexer accepts any Unicode letter, but each runtime ships its own
# Unicode tables; restricting to ASCII is what lets three languages agree on every input.
_SEGMENT = r"[A-Za-z_][A-Za-z0-9_]*"
_INDEX = r"\[(?:-?[0-9]{1,9})?\]"
_PATH = re.compile(rf"{_SEGMENT}(?:{_INDEX})*(?:\.{_SEGMENT}(?:{_INDEX})*)*")
_NAME = re.compile(_SEGMENT)

_INT64_MIN = -(2**63)
_INT64_MAX = 2**63 - 1
_INT32_MAX = 2**31 - 1


def _check_path(path: Any, what: str = "field path") -> str:
    if not isinstance(path, str) or not _PATH.fullmatch(path):
        raise ValueError(
            f"{path!r} is not a usable {what}. Use letters, digits and '_', joined by '.', "
            "with [n] or [] after a segment — e.g. customer.address.city or lines[].sku."
        )
    if path.upper() in _KEYWORDS:
        raise ValueError(f"{path!r} is a CuteQL keyword and cannot be used as a bare {what}.")
    return path


def _check_name(name: Any, what: str) -> str:
    if not isinstance(name, str) or not _NAME.fullmatch(name) or name.upper() in _KEYWORDS:
        raise ValueError(
            f"{name!r} is not a usable {what}: it must be letters, digits and '_', and not a keyword."
        )
    return name


def _check_count(value: Any, clause: str) -> int:
    # The parser accepts 0..int.MaxValue and nothing else; checking here gives the error a name.
    if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= _INT32_MAX:
        raise ValueError(f"{clause} must be an integer from 0 to {_INT32_MAX}, not {value!r}.")
    return value


def _value(value: Any) -> Any:
    """Checks a value and converts it to what the server's JSON reader expects."""
    if value is None or isinstance(value, (bool, str)):
        return value
    if isinstance(value, int):
        if not _INT64_MIN <= value <= _INT64_MAX:
            raise ValueError(f"{value} does not fit in the 64-bit integer the server reads.")
        return value
    if isinstance(value, float):
        # json would write NaN/Infinity, which is not JSON and the server would refuse.
        if not math.isfinite(value):
            raise ValueError(f"{value!r} cannot be sent: JSON has no spelling for it.")
        return value
    if isinstance(value, Decimal):
        if not value.is_finite():
            raise ValueError(f"{value!r} cannot be sent: JSON has no spelling for it.")
        return value
    if isinstance(value, datetime):
        # The server reads {"$date": …} as a datetime, assuming UTC when no offset is given, so a
        # naive datetime is taken as UTC here too. Millisecond precision with a Z, always, because
        # that is the one spelling all three clients can produce identically.
        moment = value if value.tzinfo is None else value.astimezone(timezone.utc)
        text = moment.strftime("%Y-%m-%dT%H:%M:%S") + f".{moment.microsecond // 1000:03d}Z"
        return {"$date": text}
    if isinstance(value, (list, tuple)):
        return [_value(item) for item in value]
    raise TypeError(
        f"A {type(value).__name__} cannot be bound. Use str, int, float, Decimal, bool, None, "
        "datetime, or a list of those."
    )


def escape_like(text: str) -> str:
    """Escapes ``\\``, ``%`` and ``_`` so text matches itself inside a LIKE pattern.

    The same three replacements, in the same order, as the LINQ provider's ``StartsWith``: the
    user's text is data, so a product code containing ``%`` must match itself rather than becoming
    a wildcard.
    """
    if not isinstance(text, str):
        raise TypeError(f"LIKE text must be a str, not {type(text).__name__}.")
    return text.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")


class _Binder:
    """Hands out @p0, @p1… in the order the text mentions them."""

    def __init__(self) -> None:
        self.values: dict[str, Any] = {}

    def bind(self, value: Any) -> str:
        name = f"p{len(self.values)}"
        self.values[name] = value
        return "@" + name


class Condition:
    """A predicate. Combine with ``&``, ``|`` and ``~``, or with :func:`and_`, :func:`or_`, :func:`not_`.

    Python's ``&`` binds tighter than ``==``, so parenthesise each comparison:
    ``(field("a") == 1) & (field("b") == 2)``.
    """

    def _render(self, binder: _Binder) -> str:  # pragma: no cover - abstract
        raise NotImplementedError

    def __and__(self, other: "Condition") -> "Condition":
        return and_(self, other)

    def __or__(self, other: "Condition") -> "Condition":
        return or_(self, other)

    def __invert__(self) -> "Condition":
        return not_(self)

    def __bool__(self) -> bool:
        # `a == 1 and b == 2` would silently keep only the second condition. Refuse instead.
        raise TypeError("Combine conditions with & and | (or and_/or_), not with 'and'/'or'.")


class _Binary(Condition):
    def __init__(self, path: str, operator: str, value: Any) -> None:
        self._path, self._operator, self._value = path, operator, value

    def _render(self, binder: _Binder) -> str:
        return f"{self._path} {self._operator} {binder.bind(self._value)}"


class _Between(Condition):
    def __init__(self, path: str, low: Any, high: Any, negated: bool) -> None:
        self._path, self._low, self._high, self._negated = path, low, high, negated

    def _render(self, binder: _Binder) -> str:
        keyword = "NOT BETWEEN" if self._negated else "BETWEEN"
        return f"{self._path} {keyword} {binder.bind(self._low)} AND {binder.bind(self._high)}"


class _Is(Condition):
    def __init__(self, path: str, suffix: str) -> None:
        self._path, self._suffix = path, suffix

    def _render(self, binder: _Binder) -> str:
        return f"{self._path} IS {self._suffix}"


class _Logical(Condition):
    def __init__(self, operator: str, children: list[Condition]) -> None:
        self._operator, self._children = operator, children

    def _render(self, binder: _Binder) -> str:
        # A nested AND/OR is always bracketed. Precedence would make some of those brackets
        # redundant, but always writing them is a rule all three builders can follow identically.
        parts = [
            f"({child._render(binder)})" if isinstance(child, _Logical) else child._render(binder)
            for child in self._children
        ]
        return f" {self._operator} ".join(parts)


class _Not(Condition):
    def __init__(self, inner: Condition) -> None:
        self._inner = inner

    def _render(self, binder: _Binder) -> str:
        return f"NOT ({self._inner._render(binder)})"


def _logical(operator: str, conditions: Iterable[Condition]) -> Condition:
    children: list[Condition] = []
    for condition in conditions:
        if not isinstance(condition, Condition):
            raise TypeError(f"{operator} takes conditions, not {type(condition).__name__}.")
        # (a AND b) AND c is a AND b AND c; flattening keeps the brackets for where they matter.
        if isinstance(condition, _Logical) and condition._operator == operator:
            children.extend(condition._children)
        else:
            children.append(condition)
    if not children:
        raise ValueError(f"{operator} needs at least one condition.")
    return children[0] if len(children) == 1 else _Logical(operator, children)


def and_(*conditions: Condition) -> Condition:
    """All of the conditions."""
    return _logical("AND", conditions)


def or_(*conditions: Condition) -> Condition:
    """Any of the conditions."""
    return _logical("OR", conditions)


def not_(condition: Condition) -> Condition:
    """The negation of a condition. Remember MISSING: ``NOT (x > 0)`` still skips rows without x."""
    if not isinstance(condition, Condition):
        raise TypeError(f"not_ takes a condition, not {type(condition).__name__}.")
    return _Not(condition)


class Field:
    """A field path — ``total``, ``customer.address.city``, ``lines[0].sku``, ``lines[].sku``.

    The comparison operators build conditions, so ``field("total") > 5`` is a :class:`Condition`,
    not a bool. A field holding an array compares element-wise: ``field("tags") == "promo"``
    means "tags contains promo".
    """

    __slots__ = ("path",)
    __hash__ = None  # type: ignore[assignment]  # == builds a condition, so a Field is not hashable

    def __init__(self, path: str) -> None:
        self.path = _check_path(path)

    def __repr__(self) -> str:
        return f"field({self.path!r})"

    # -- comparison ------------------------------------------------------------------------

    def eq(self, value: Any) -> Condition:
        """``= value``. Against ``None`` this is ``IS NULL``, because ``= NULL`` is never true."""
        return _Is(self.path, "NULL") if value is None else _Binary(self.path, "=", _value(value))

    def ne(self, value: Any) -> Condition:
        """``!= value``. Against ``None`` this is ``IS NOT NULL``."""
        return _Is(self.path, "NOT NULL") if value is None else _Binary(self.path, "!=", _value(value))

    def _ordered(self, operator: str, value: Any) -> Condition:
        if value is None:
            raise ValueError(f"'{self.path} {operator} NULL' is never true; use is_null() instead.")
        return _Binary(self.path, operator, _value(value))

    def lt(self, value: Any) -> Condition:
        return self._ordered("<", value)

    def le(self, value: Any) -> Condition:
        return self._ordered("<=", value)

    def gt(self, value: Any) -> Condition:
        return self._ordered(">", value)

    def ge(self, value: Any) -> Condition:
        return self._ordered(">=", value)

    __eq__ = eq  # type: ignore[assignment]
    __ne__ = ne  # type: ignore[assignment]
    __lt__ = lt
    __le__ = le
    __gt__ = gt
    __ge__ = ge

    # -- membership and range --------------------------------------------------------------

    def is_in(self, values: Iterable[Any]) -> Condition:
        """``IN`` the given values, sent as one array parameter."""
        return _Binary(self.path, "IN", self._list(values))

    def not_in(self, values: Iterable[Any]) -> Condition:
        return _Binary(self.path, "NOT IN", self._list(values))

    def between(self, low: Any, high: Any) -> Condition:
        """Inclusive at both ends."""
        return _Between(self.path, self._bound(low), self._bound(high), negated=False)

    def not_between(self, low: Any, high: Any) -> Condition:
        return _Between(self.path, self._bound(low), self._bound(high), negated=True)

    # -- text ------------------------------------------------------------------------------

    def like(self, pattern: str) -> Condition:
        """A raw LIKE pattern: ``%`` and ``_`` are wildcards. Use the helpers for user text."""
        return _Binary(self.path, "LIKE", self._text(pattern))

    def not_like(self, pattern: str) -> Condition:
        return _Binary(self.path, "NOT LIKE", self._text(pattern))

    def startswith(self, text: str) -> Condition:
        """Begins with ``text`` taken literally — its ``%`` and ``_`` are escaped."""
        return _Binary(self.path, "LIKE", escape_like(text) + "%")

    def endswith(self, text: str) -> Condition:
        return _Binary(self.path, "LIKE", "%" + escape_like(text))

    def contains(self, text: str) -> Condition:
        """Has ``text`` as a substring. For "array contains value", use ``==``."""
        return _Binary(self.path, "LIKE", "%" + escape_like(text) + "%")

    # -- presence --------------------------------------------------------------------------

    def is_null(self) -> Condition:
        """Absent or explicitly null."""
        return _Is(self.path, "NULL")

    def is_not_null(self) -> Condition:
        return _Is(self.path, "NOT NULL")

    def is_missing(self) -> Condition:
        """Absent — not written at all. Distinct from null."""
        return _Is(self.path, "MISSING")

    def is_not_missing(self) -> Condition:
        return _Is(self.path, "NOT MISSING")

    # -- helpers ---------------------------------------------------------------------------

    @staticmethod
    def _list(values: Iterable[Any]) -> list[Any]:
        if isinstance(values, (str, bytes, Mapping)):
            raise TypeError("IN takes a list of values, not a single string or mapping.")
        return [_value(item) for item in values]

    def _bound(self, value: Any) -> Any:
        if value is None:
            raise ValueError(f"BETWEEN on '{self.path}' needs two non-null bounds.")
        return _value(value)

    @staticmethod
    def _text(pattern: Any) -> str:
        if not isinstance(pattern, str):
            raise TypeError(f"LIKE takes a str pattern, not {type(pattern).__name__}.")
        return pattern


def field(path: str) -> Field:
    """A field path to build conditions on: ``field("address.city") == "Bandung"``."""
    return Field(path)


class Query:
    """A SELECT over one collection.

    Each method changes the query and returns it, so calls chain. Create one with
    ``collection.query()`` to be able to :meth:`run` it, or ``Query("orders")`` to only render it.
    """

    def __init__(self, collection: str, client: "CuteClient | None" = None) -> None:
        self.collection = _check_name(collection, "collection name")
        self._client = client
        self._projections: list[str] = []
        self._where: list[Condition] = []
        self._order: list[str] = []
        self._limit: int | None = None
        self._offset = 0

    def __repr__(self) -> str:
        return f"Query({self.to_cuteql()!r})"

    def select(self, *paths: str, **aliases: str) -> "Query":
        """Project fields. ``select("code", buyer="customer.name")`` → ``code, customer.name AS buyer``.

        Positional paths come first, then aliased ones in keyword order.
        """
        for path in paths:
            self._projections.append(_check_path(path))
        for alias, path in aliases.items():
            self._projections.append(f"{_check_path(path)} AS {_check_name(alias, 'alias')}")
        return self

    def where(self, *conditions: Condition) -> "Query":
        """Filter. Several conditions, or several calls, are combined with AND."""
        for condition in conditions:
            if not isinstance(condition, Condition):
                raise TypeError(
                    f"where() takes conditions such as field('total') > 5, not {type(condition).__name__}."
                )
            self._where.append(condition)
        return self

    def order_by(self, path: str, *, desc: bool = False) -> "Query":
        """Sort. Call again for a second key."""
        self._order.append(_check_path(path) + (" DESC" if desc else ""))
        return self

    def limit(self, count: int) -> "Query":
        self._limit = _check_count(count, "LIMIT")
        return self

    def offset(self, count: int) -> "Query":
        self._offset = _check_count(count, "OFFSET")
        return self

    # -- rendering -------------------------------------------------------------------------

    def build(self) -> tuple[str, dict[str, Any]]:
        """The CuteQL text and the parameters it refers to."""
        binder = _Binder()
        text = "SELECT " + (", ".join(self._projections) if self._projections else "*")
        text += " FROM " + self.collection
        if self._where:
            text += " WHERE " + and_(*self._where)._render(binder)
        if self._order:
            text += " ORDER BY " + ", ".join(self._order)
        if self._limit is not None:
            text += f" LIMIT {self._limit}"
        if self._offset:
            text += f" OFFSET {self._offset}"
        return text, binder.values

    def to_cuteql(self) -> str:
        """The statement this query becomes, with ``@p0``-style placeholders for its values."""
        return self.build()[0]

    @property
    def parameters(self) -> dict[str, Any]:
        """The values bound to the placeholders, as they will be sent."""
        return self.build()[1]

    # -- running ---------------------------------------------------------------------------

    def run(self) -> "QueryResult":
        """Run the query on the server it was created from."""
        return self._require_client().query(self)

    def explain(self) -> dict[str, Any]:
        """How the server would find the rows, without returning them."""
        text, parameters = self.build()
        return self._require_client().explain(text, parameters)

    def _require_client(self) -> "CuteClient":
        if self._client is None:
            raise ValueError("This query has no client. Create it with collection.query(), or pass it to db.query().")
        return self._client
