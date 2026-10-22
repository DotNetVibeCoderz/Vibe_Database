package cutedb

// A query builder that renders CuteQL with every value bound as a parameter.
//
// Concatenating a filter by hand is how injection bugs are written. This builds the statement from
// parts instead, asks it what CuteQL it becomes, and runs it. Two rules make it safe, and they are
// the whole design:
//
//   - Values never enter the text. Every value becomes @p0, @p1… and travels in the request's
//     parameters, which the server binds as values. There is no escaping to get wrong because
//     nothing is escaped.
//   - Names are checked against the lexer. A field path, alias or collection name is part of the
//     text, so it must lex as exactly one identifier. Anything else is refused rather than quoted —
//     CuteQL has no quoted identifiers, so there is nothing safe to quote it into.
//
// The rendering is shared with the Python and Node builders and pinned by
// clients/querybuilder-golden.json, which the .NET test suite parses and runs.

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"reflect"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// keywords is CuteLexer.Keywords. A single bare word on this list lexes as a keyword rather than
// an identifier, so "count" or "order" cannot name a field — but "customer.count" can, because a
// word containing '.' or '[' is always an identifier.
var keywords = map[string]bool{}

func init() {
	for _, word := range strings.Fields(`
		SELECT FROM WHERE ORDER BY ASC DESC LIMIT OFFSET GROUP HAVING AS AND OR NOT IN LIKE BETWEEN IS
		NULL TRUE FALSE MISSING DELETE UPDATE SET INSERT INTO VALUES DISTINCT COUNT SUM AVG MIN MAX
		EXISTS`) {
		keywords[word] = true
	}
}

// ASCII only, deliberately. The lexer accepts any Unicode letter, but each runtime ships its own
// Unicode tables; restricting to ASCII is what lets three languages agree on every input.
const (
	segmentPattern = `[A-Za-z_][A-Za-z0-9_]*`
	indexPattern   = `\[(?:-?[0-9]{1,9})?\]`
)

var (
	pathRegexp = regexp.MustCompile(`^` + segmentPattern + `(?:` + indexPattern + `)*(?:\.` + segmentPattern + `(?:` + indexPattern + `)*)*$`)
	nameRegexp = regexp.MustCompile(`^` + segmentPattern + `$`)
)

// ErrInvalidQuery wraps every error the builder reports, so callers can tell a query that was
// refused before it was sent from one the server refused.
var ErrInvalidQuery = errors.New("cutedb: invalid query")

func invalid(format string, args ...any) error {
	return fmt.Errorf("%w: %s", ErrInvalidQuery, fmt.Sprintf(format, args...))
}

func checkPath(path, what string) error {
	if !pathRegexp.MatchString(path) {
		return invalid("%q is not a usable %s. Use letters, digits and '_', joined by '.', with [n] or [] after a segment — e.g. customer.address.city or lines[].sku", path, what)
	}
	if keywords[strings.ToUpper(path)] {
		return invalid("%q is a CuteQL keyword and cannot be used as a bare %s", path, what)
	}
	return nil
}

func checkName(name, what string) error {
	if !nameRegexp.MatchString(name) || keywords[strings.ToUpper(name)] {
		return invalid("%q is not a usable %s: it must be letters, digits and '_', and not a keyword", name, what)
	}
	return nil
}

// bindable checks a value and converts it to what the server's JSON reader expects.
func bindable(value any) (any, error) {
	switch v := value.(type) {
	case nil:
		return nil, nil
	case json.Number:
		if _, err := strconv.ParseFloat(string(v), 64); err != nil {
			return nil, invalid("%q is not a number", string(v))
		}
		return v, nil
	case time.Time:
		// The server reads {"$date": …} as a datetime. Millisecond precision with a Z, always,
		// because that is the one spelling all three clients can produce identically.
		return map[string]any{"$date": v.UTC().Format("2006-01-02T15:04:05.000Z")}, nil
	case []byte:
		return nil, invalid("[]byte cannot be bound; convert it to a string if it is text")
	}

	// Reflection rather than a type switch, so named types — type Status string — work too.
	reflected := reflect.ValueOf(value)
	switch reflected.Kind() {
	case reflect.Bool:
		return reflected.Bool(), nil
	case reflect.String:
		return reflected.String(), nil
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return reflected.Int(), nil
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64, reflect.Uintptr:
		if reflected.Uint() > math.MaxInt64 {
			return nil, invalid("%d does not fit in the 64-bit integer the server reads", reflected.Uint())
		}
		return int64(reflected.Uint()), nil
	case reflect.Float32, reflect.Float64:
		number := reflected.Float()
		if math.IsNaN(number) || math.IsInf(number, 0) {
			return nil, invalid("%v cannot be sent: JSON has no spelling for it", number)
		}
		return number, nil
	case reflect.Slice, reflect.Array:
		items := make([]any, reflected.Len())
		for i := range items {
			item, err := bindable(reflected.Index(i).Interface())
			if err != nil {
				return nil, err
			}
			items[i] = item
		}
		return items, nil
	case reflect.Pointer:
		if reflected.IsNil() {
			return nil, nil
		}
		return bindable(reflected.Elem().Interface())
	}

	return nil, invalid("a %T cannot be bound. Use a string, number, bool, nil, time.Time, or a slice of those", value)
}

// EscapeLike escapes \, % and _ so text matches itself inside a LIKE pattern.
//
// The same three replacements, in the same order, as the LINQ provider's StartsWith: the user's
// text is data, so a product code containing % must match itself rather than becoming a wildcard.
func EscapeLike(text string) string {
	return strings.NewReplacer(`\`, `\\`, `%`, `\%`, `_`, `\_`).Replace(text)
}

// binder hands out @p0, @p1… in the order the text mentions them.
type binder struct {
	values map[string]any
}

func (b *binder) bind(value any) string {
	name := "p" + strconv.Itoa(len(b.values))
	b.values[name] = value
	return "@" + name
}

// Condition is a predicate, built from a [FieldPath] and combined with [And], [Or] and [Not].
type Condition interface {
	render(b *binder) string
	invalid() error
}

type binaryCondition struct {
	path, operator string
	value          any
	err            error
}

func (c binaryCondition) render(b *binder) string {
	return c.path + " " + c.operator + " " + b.bind(c.value)
}

func (c binaryCondition) invalid() error { return c.err }

type betweenCondition struct {
	path      string
	low, high any
	negated   bool
	err       error
}

func (c betweenCondition) render(b *binder) string {
	keyword := " BETWEEN "
	if c.negated {
		keyword = " NOT BETWEEN "
	}
	return c.path + keyword + b.bind(c.low) + " AND " + b.bind(c.high)
}

func (c betweenCondition) invalid() error { return c.err }

type isCondition struct {
	path, suffix string
	err          error
}

func (c isCondition) render(*binder) string { return c.path + " IS " + c.suffix }

func (c isCondition) invalid() error { return c.err }

type logicalCondition struct {
	operator string
	children []Condition
	err      error
}

func (c logicalCondition) render(b *binder) string {
	parts := make([]string, len(c.children))
	for i, child := range c.children {
		// A nested AND/OR is always bracketed. Precedence would make some of those brackets
		// redundant, but always writing them is a rule all three builders can follow identically.
		if _, nested := child.(logicalCondition); nested {
			parts[i] = "(" + child.render(b) + ")"
		} else {
			parts[i] = child.render(b)
		}
	}
	return strings.Join(parts, " "+c.operator+" ")
}

func (c logicalCondition) invalid() error {
	if c.err != nil {
		return c.err
	}
	for _, child := range c.children {
		if err := child.invalid(); err != nil {
			return err
		}
	}
	return nil
}

type notCondition struct {
	inner Condition
}

func (c notCondition) render(b *binder) string { return "NOT (" + c.inner.render(b) + ")" }

func (c notCondition) invalid() error { return c.inner.invalid() }

func logical(operator string, conditions []Condition) Condition {
	var children []Condition
	for _, condition := range conditions {
		if condition == nil {
			return logicalCondition{operator: operator, err: invalid("%s was given a nil condition", operator)}
		}
		// (a AND b) AND c is a AND b AND c; flattening keeps the brackets for where they matter.
		if same, ok := condition.(logicalCondition); ok && same.operator == operator && same.err == nil {
			children = append(children, same.children...)
		} else {
			children = append(children, condition)
		}
	}

	switch len(children) {
	case 0:
		return logicalCondition{operator: operator, err: invalid("%s needs at least one condition", operator)}
	case 1:
		return children[0]
	default:
		return logicalCondition{operator: operator, children: children}
	}
}

// And is true when every condition is.
func And(conditions ...Condition) Condition { return logical("AND", conditions) }

// Or is true when any condition is.
func Or(conditions ...Condition) Condition { return logical("OR", conditions) }

// Not negates a condition. Remember MISSING: NOT (x > 0) still skips rows without x.
func Not(condition Condition) Condition {
	if condition == nil {
		return logicalCondition{operator: "NOT", err: invalid("Not was given a nil condition")}
	}
	return notCondition{inner: condition}
}

// FieldPath is a field to build conditions on — total, customer.address.city, lines[0].sku,
// lines[].sku. Create one with [Field].
//
// A field holding an array compares element-wise: Field("tags").Eq("promo") means "tags contains
// promo".
type FieldPath struct {
	path string
	err  error
}

// Field names a field path. An invalid path is reported when the query is built, not here, so
// calls chain without an error check at every step.
func Field(path string) FieldPath {
	return FieldPath{path: path, err: checkPath(path, "field path")}
}

// Path returns the path as written.
func (f FieldPath) Path() string { return f.path }

func (f FieldPath) binary(operator string, value any) Condition {
	bound, err := bindable(value)
	if f.err != nil {
		err = f.err
	}
	return binaryCondition{path: f.path, operator: operator, value: bound, err: err}
}

func (f FieldPath) ordered(operator string, value any) Condition {
	if value == nil {
		return binaryCondition{path: f.path, err: invalid("'%s %s NULL' is never true; use IsNull instead", f.path, operator)}
	}
	return f.binary(operator, value)
}

func (f FieldPath) is(suffix string) Condition {
	return isCondition{path: f.path, suffix: suffix, err: f.err}
}

// Eq is "= value". Against nil it is IS NULL, because = NULL is never true.
func (f FieldPath) Eq(value any) Condition {
	if value == nil {
		return f.is("NULL")
	}
	return f.binary("=", value)
}

// Ne is "!= value". Against nil it is IS NOT NULL.
func (f FieldPath) Ne(value any) Condition {
	if value == nil {
		return f.is("NOT NULL")
	}
	return f.binary("!=", value)
}

// Lt is "< value".
func (f FieldPath) Lt(value any) Condition { return f.ordered("<", value) }

// Le is "<= value".
func (f FieldPath) Le(value any) Condition { return f.ordered("<=", value) }

// Gt is "> value".
func (f FieldPath) Gt(value any) Condition { return f.ordered(">", value) }

// Ge is ">= value".
func (f FieldPath) Ge(value any) Condition { return f.ordered(">=", value) }

// listOf lets In take either values or one slice: In("a", "b") and In(names) both mean a list.
func listOf(values []any) any {
	if len(values) == 1 && values[0] != nil {
		kind := reflect.TypeOf(values[0]).Kind()
		if _, isBytes := values[0].([]byte); !isBytes && (kind == reflect.Slice || kind == reflect.Array) {
			return values[0]
		}
	}
	return values
}

// In is true when the field equals any of the values, sent as one array parameter. Pass the
// values, or a single slice of them.
func (f FieldPath) In(values ...any) Condition { return f.binary("IN", listOf(values)) }

// NotIn is the negation of In.
func (f FieldPath) NotIn(values ...any) Condition { return f.binary("NOT IN", listOf(values)) }

func (f FieldPath) between(low, high any, negated bool) Condition {
	condition := betweenCondition{path: f.path, negated: negated, err: f.err}
	if low == nil || high == nil {
		condition.err = invalid("BETWEEN on '%s' needs two non-nil bounds", f.path)
		return condition
	}

	var errLow, errHigh error
	condition.low, errLow = bindable(low)
	condition.high, errHigh = bindable(high)
	if condition.err == nil {
		condition.err = errors.Join(errLow, errHigh)
	}
	return condition
}

// Between is inclusive at both ends.
func (f FieldPath) Between(low, high any) Condition { return f.between(low, high, false) }

// NotBetween is the negation of Between.
func (f FieldPath) NotBetween(low, high any) Condition { return f.between(low, high, true) }

// Like matches a raw LIKE pattern: % and _ are wildcards. Use StartsWith, EndsWith or Contains
// for text that came from a user.
func (f FieldPath) Like(pattern string) Condition { return f.binary("LIKE", pattern) }

// NotLike is the negation of Like.
func (f FieldPath) NotLike(pattern string) Condition { return f.binary("NOT LIKE", pattern) }

// StartsWith matches text taken literally — its % and _ are escaped.
func (f FieldPath) StartsWith(text string) Condition {
	return f.binary("LIKE", EscapeLike(text)+"%")
}

// EndsWith matches text taken literally at the end.
func (f FieldPath) EndsWith(text string) Condition {
	return f.binary("LIKE", "%"+EscapeLike(text))
}

// Contains matches text as a substring. For "array contains value", use Eq.
func (f FieldPath) Contains(text string) Condition {
	return f.binary("LIKE", "%"+EscapeLike(text)+"%")
}

// IsNull is true when the field is absent or explicitly null.
func (f FieldPath) IsNull() Condition { return f.is("NULL") }

// IsNotNull is true when the field holds a non-null value.
func (f FieldPath) IsNotNull() Condition { return f.is("NOT NULL") }

// IsMissing is true when the field is absent — not written at all. Distinct from null.
func (f FieldPath) IsMissing() Condition { return f.is("MISSING") }

// IsNotMissing is true when the field is present, whatever its value.
func (f FieldPath) IsNotMissing() Condition { return f.is("NOT MISSING") }

// Query is a SELECT over one collection, built a piece at a time.
//
// Each method changes the query and returns it, so calls chain. Errors — a bad field name, a value
// that cannot be bound — are kept and reported by Build, ToCuteQL and Run, so a chain needs one
// error check rather than one per call.
//
//	result, err := orders.Query().
//	    Where(cutedb.Field("address.city").Eq(city), cutedb.Field("total").Gt(500000)).
//	    OrderByDesc("total").
//	    Limit(10).
//	    Run(ctx)
type Query struct {
	collection  string
	client      *Client
	projections []string
	where       []Condition
	order       []string
	limit       int
	hasLimit    bool
	offset      int
	err         error
}

// From starts a query that can be rendered with ToCuteQL, and run with [Client.Run].
func From(collection string) *Query {
	return &Query{collection: collection}
}

// Query starts a query over this collection that can be run directly with [Query.Run].
//
// Prefer it to Find with a hand-written filter whenever a value comes from outside the program:
// the builder binds every value as a parameter, so none can become syntax.
func (c *Collection) Query() *Query {
	return &Query{collection: c.name, client: c.client}
}

func (q *Query) fail(err error) *Query {
	if q.err == nil {
		q.err = err
	}
	return q
}

// Select projects fields, in order.
func (q *Query) Select(paths ...string) *Query {
	for _, path := range paths {
		if err := checkPath(path, "field path"); err != nil {
			return q.fail(err)
		}
		q.projections = append(q.projections, path)
	}
	return q
}

// SelectAs projects one field under another name: SelectAs("customer.name", "buyer").
func (q *Query) SelectAs(path, alias string) *Query {
	if err := checkPath(path, "field path"); err != nil {
		return q.fail(err)
	}
	if err := checkName(alias, "alias"); err != nil {
		return q.fail(err)
	}
	q.projections = append(q.projections, path+" AS "+alias)
	return q
}

// Where filters. Several conditions, or several calls, are combined with AND.
func (q *Query) Where(conditions ...Condition) *Query {
	for _, condition := range conditions {
		if condition == nil {
			return q.fail(invalid("Where was given a nil condition"))
		}
		q.where = append(q.where, condition)
	}
	return q
}

// OrderBy sorts ascending. Call again for a second key.
func (q *Query) OrderBy(path string) *Query {
	if err := checkPath(path, "field path"); err != nil {
		return q.fail(err)
	}
	q.order = append(q.order, path)
	return q
}

// OrderByDesc sorts descending.
func (q *Query) OrderByDesc(path string) *Query {
	if err := checkPath(path, "field path"); err != nil {
		return q.fail(err)
	}
	q.order = append(q.order, path+" DESC")
	return q
}

// The parser accepts 0..int.MaxValue and nothing else; checking here gives the error a name.
func checkCount(count int, clause string) error {
	if count < 0 || int64(count) > math.MaxInt32 {
		return invalid("%s must be from 0 to %d, not %d", clause, math.MaxInt32, count)
	}
	return nil
}

// Limit caps the rows returned.
func (q *Query) Limit(count int) *Query {
	if err := checkCount(count, "LIMIT"); err != nil {
		return q.fail(err)
	}
	q.limit, q.hasLimit = count, true
	return q
}

// Offset skips rows, for paging.
func (q *Query) Offset(count int) *Query {
	if err := checkCount(count, "OFFSET"); err != nil {
		return q.fail(err)
	}
	q.offset = count
	return q
}

// Build returns the CuteQL text and the parameters it refers to.
func (q *Query) Build() (string, map[string]any, error) {
	if q.err != nil {
		return "", nil, q.err
	}
	if err := checkName(q.collection, "collection name"); err != nil {
		return "", nil, err
	}

	var where Condition
	if len(q.where) > 0 {
		where = And(q.where...)
		if err := where.invalid(); err != nil {
			return "", nil, err
		}
	}

	b := &binder{values: map[string]any{}}
	var text strings.Builder
	text.WriteString("SELECT ")
	if len(q.projections) == 0 {
		text.WriteString("*")
	} else {
		text.WriteString(strings.Join(q.projections, ", "))
	}
	text.WriteString(" FROM " + q.collection)
	if where != nil {
		text.WriteString(" WHERE " + where.render(b))
	}
	if len(q.order) > 0 {
		text.WriteString(" ORDER BY " + strings.Join(q.order, ", "))
	}
	if q.hasLimit {
		text.WriteString(" LIMIT " + strconv.Itoa(q.limit))
	}
	if q.offset > 0 {
		text.WriteString(" OFFSET " + strconv.Itoa(q.offset))
	}

	return text.String(), b.values, nil
}

// ToCuteQL returns the statement this query becomes, with @p0-style placeholders for its values.
func (q *Query) ToCuteQL() (string, error) {
	text, _, err := q.Build()
	return text, err
}

// Run runs the query on the server of the collection it was created from.
func (q *Query) Run(ctx context.Context) (QueryResult, error) {
	if q.client == nil {
		return QueryResult{}, invalid("this query has no client; create it with Collection.Query, or pass it to Client.Run")
	}
	return q.client.Run(ctx, q)
}

// Explain reports how the server would find the rows, without returning them.
func (q *Query) Explain(ctx context.Context) (map[string]any, error) {
	if q.client == nil {
		return nil, invalid("this query has no client; create it with Collection.Query")
	}
	text, parameters, err := q.Build()
	if err != nil {
		return nil, err
	}
	return q.client.Explain(ctx, text, parameters)
}

// Run runs a built query, sending its values as parameters.
func (c *Client) Run(ctx context.Context, q *Query) (QueryResult, error) {
	text, parameters, err := q.Build()
	if err != nil {
		return QueryResult{}, err
	}
	return c.Query(ctx, text, parameters)
}
