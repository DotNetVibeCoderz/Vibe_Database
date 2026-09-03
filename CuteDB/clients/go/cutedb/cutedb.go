// Package cutedb is a Go client for CuteDB, the cute embedded document database.
//
// Built by Gravicode Studios, led by Kang Fadhil.
// https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB
//
// CuteDB is an embedded database, so this package talks to cutedb-server over HTTP rather than
// binding to the engine directly. One HTTP endpoint is a far smaller surface to keep correct
// across three languages and six platforms than three sets of cgo bindings would be, and the
// network hop is irrelevant next to the work most calls do.
//
// Documents are represented as map[string]any, because a CuteDB collection has no schema to
// generate a struct from. Use [Decode] to unmarshal a document into a struct of your own when the
// shape is known.
//
//	client := cutedb.New("http://127.0.0.1:8420")
//
//	orders := client.Collection("orders")
//	if _, err := orders.Insert(ctx, cutedb.Document{"customer": "Sari", "total": 249000}); err != nil {
//	    log.Fatal(err)
//	}
//
//	result, err := client.Query(ctx,
//	    "SELECT address.city AS city, SUM(total) AS revenue FROM orders GROUP BY address.city",
//	    nil)
//
// Every method takes a context. Cancelling it cancels the underlying request.
package cutedb

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// Version of this client.
const Version = "2.0.0"

// Document is one CuteDB document. The reserved "_id" key holds its identifier.
type Document map[string]any

// ID returns the document's identifier, or "" when it has none.
func (d Document) ID() string {
	if raw, ok := d["_id"].(string); ok {
		return raw
	}
	return ""
}

// Error is anything the server refused.
type Error struct {
	// Code is the server's machine-readable error code, such as "invalid_query".
	Code string
	// Message is written to be shown to a person. For a query error it includes a caret line
	// pointing at the offending character, so printing it directly is usually right.
	Message string
	// Status is the HTTP status code.
	Status int
}

func (e *Error) Error() string {
	if e.Code == "" {
		return e.Message
	}
	return fmt.Sprintf("cutedb: %s: %s", e.Code, e.Message)
}

// IsNotFound reports whether err is a 404 from the server, which is how a missing document,
// collection or index is signalled.
func IsNotFound(err error) bool {
	var apiError *Error
	return errors.As(err, &apiError) && apiError.Status == http.StatusNotFound
}

// IsQueryError reports whether err is CuteQL that would not parse or would not run.
func IsQueryError(err error) bool {
	var apiError *Error
	return errors.As(err, &apiError) && apiError.Code == "invalid_query"
}

// IndexInfo describes one secondary index.
type IndexInfo struct {
	Name    string `json:"name"`
	Path    string `json:"path"`
	Unique  bool   `json:"unique"`
	Keys    int    `json:"keys"`
	Entries int    `json:"entries"`
}

// QueryResult is what a statement produced.
type QueryResult struct {
	// Kind is "select", "insert", "update" or "delete".
	Kind string `json:"kind"`
	// Columns are the field names present across the returned rows, in first-seen order.
	// A collection has no schema, so these are discovered from the data rather than declared.
	Columns []string `json:"columns"`
	// Rows are the returned documents. Empty for anything but a SELECT.
	Rows []Document `json:"rows"`
	// Affected counts documents inserted, updated or deleted; for a SELECT, the row count.
	Affected int `json:"affected"`
	// DurationMs is how long the statement took on the server.
	DurationMs float64 `json:"durationMs"`
	// Plan describes how the rows were found.
	Plan string `json:"plan"`
}

// Scalar returns the single value of a one-row, one-column result — what an aggregate returns.
func (r QueryResult) Scalar() any {
	if len(r.Rows) == 0 || len(r.Columns) == 0 {
		return nil
	}
	return r.Rows[0][r.Columns[0]]
}

// Stats are totals across a database.
type Stats struct {
	Path              string    `json:"path"`
	Collections       int       `json:"collections"`
	Documents         int       `json:"documents"`
	FileBytes         int64     `json:"fileBytes"`
	LiveBytes         int64     `json:"liveBytes"`
	DeadBytes         int64     `json:"deadBytes"`
	ReservedBytes     int64     `json:"reservedBytes"`
	FileAmplification float64   `json:"fileAmplification"`
	CreatedAt         time.Time `json:"createdAt"`
	Engine            string    `json:"engine"`
}

// CollectionStats describes one collection.
type CollectionStats struct {
	Name                 string      `json:"name"`
	Documents            int         `json:"documents"`
	LiveBytes            int64       `json:"liveBytes"`
	DeadBytes            int64       `json:"deadBytes"`
	ReservedBytes        int64       `json:"reservedBytes"`
	AverageDocumentBytes float64     `json:"averageDocumentBytes"`
	Indexes              []IndexInfo `json:"indexes"`
}

// Client is a connection to a CuteDB server.
//
// The client holds no session state, so one instance is safe to share across goroutines and
// should be reused rather than created per call — that is what lets the underlying transport pool
// its connections.
type Client struct {
	baseURL string
	apiKey  string
	http    *http.Client
}

// Option configures a Client.
type Option func(*Client)

// WithAPIKey sends the given key with every request.
func WithAPIKey(key string) Option {
	return func(c *Client) { c.apiKey = key }
}

// WithHTTPClient supplies the HTTP client to use, for callers that need their own transport,
// timeouts or instrumentation.
func WithHTTPClient(client *http.Client) Option {
	return func(c *Client) { c.http = client }
}

// WithTimeout sets a per-request timeout. Ignored when WithHTTPClient is also given.
func WithTimeout(timeout time.Duration) Option {
	return func(c *Client) { c.http.Timeout = timeout }
}

// New creates a client for a server.
func New(baseURL string, options ...Option) *Client {
	client := &Client{
		baseURL: strings.TrimRight(baseURL, "/"),
		http:    &http.Client{Timeout: 30 * time.Second},
	}

	for _, option := range options {
		option(client)
	}

	return client
}

// Health checks that the server is up and reports which engine build it is running.
func (c *Client) Health(ctx context.Context) (map[string]any, error) {
	var result map[string]any
	return result, c.do(ctx, http.MethodGet, "/health", nil, nil, &result)
}

// Collection returns a handle to a collection. It is created on first insert if it does not exist.
func (c *Client) Collection(name string) *Collection {
	return &Collection{client: c, name: name}
}

// Collections lists every collection with its size.
func (c *Client) Collections(ctx context.Context) ([]CollectionStats, error) {
	var result []CollectionStats
	return result, c.do(ctx, http.MethodGet, "/v1/collections", nil, nil, &result)
}

// DropCollection drops a collection and everything in it. It reports whether one was there.
func (c *Client) DropCollection(ctx context.Context, name string) (bool, error) {
	err := c.do(ctx, http.MethodDelete, "/v1/collections/"+url.PathEscape(name), nil, nil, nil)
	if IsNotFound(err) {
		return false, nil
	}
	return err == nil, err
}

// Query runs a CuteQL statement.
//
// Bind values through parameters rather than building the statement by concatenation: a bound
// value is used as a value and can never be reinterpreted as syntax.
func (c *Client) Query(ctx context.Context, query string, parameters map[string]any) (QueryResult, error) {
	body := map[string]any{"query": query}
	if len(parameters) > 0 {
		body["parameters"] = parameters
	}

	var result QueryResult
	return result, c.do(ctx, http.MethodPost, "/v1/query", nil, body, &result)
}

// Explain reports how a SELECT would find its rows, without returning them.
func (c *Client) Explain(ctx context.Context, query string, parameters map[string]any) (map[string]any, error) {
	body := map[string]any{"query": query}
	if len(parameters) > 0 {
		body["parameters"] = parameters
	}

	var result map[string]any
	return result, c.do(ctx, http.MethodPost, "/v1/explain", nil, body, &result)
}

// Stats returns totals across the database.
func (c *Client) Stats(ctx context.Context) (Stats, error) {
	var result Stats
	return result, c.do(ctx, http.MethodGet, "/v1/stats", nil, nil, &result)
}

// Compact rewrites the file with only current state and returns the bytes reclaimed.
func (c *Client) Compact(ctx context.Context) (int64, error) {
	var result struct {
		Reclaimed int64 `json:"reclaimedBytes"`
	}

	return result.Reclaimed, c.do(ctx, http.MethodPost, "/v1/compact", nil, nil, &result)
}

// Collection is a handle to one collection.
type Collection struct {
	client *Client
	name   string
}

// Name of the collection.
func (c *Collection) Name() string { return c.name }

func (c *Collection) path(suffix string) string {
	return "/v1/collections/" + url.PathEscape(c.name) + suffix
}

// Insert stores one document and returns it with the id the server assigned.
func (c *Collection) Insert(ctx context.Context, document Document) (Document, error) {
	var result Document
	return result, c.client.do(ctx, http.MethodPost, c.path("/documents"), nil, document, &result)
}

// InsertMany stores many documents in one request and returns their ids.
//
// Much faster than a loop over Insert: the server applies the whole batch under a single lock and
// flushes once, rather than once per document.
func (c *Collection) InsertMany(ctx context.Context, documents []Document) ([]string, error) {
	if len(documents) == 0 {
		return nil, nil
	}

	var result struct {
		Inserted int      `json:"inserted"`
		IDs      []string `json:"ids"`
	}

	err := c.client.do(ctx, http.MethodPost, c.path("/documents"), nil, documents, &result)
	return result.IDs, err
}

// Get fetches a document by id. A missing document returns nil with no error.
func (c *Collection) Get(ctx context.Context, id string) (Document, error) {
	var result Document
	err := c.client.do(ctx, http.MethodGet, c.path("/documents/"+url.PathEscape(id)), nil, nil, &result)
	if IsNotFound(err) {
		return nil, nil
	}
	return result, err
}

// Replace overwrites a document wholesale. The id in the URL wins over any _id in the body.
func (c *Collection) Replace(ctx context.Context, id string, document Document) (Document, error) {
	var result Document
	return result, c.client.do(ctx, http.MethodPut, c.path("/documents/"+url.PathEscape(id)), nil, document, &result)
}

// Patch merges fields into a document.
//
// A key containing a dot is treated as a path, so {"address.city": "Bandung"} reaches into the
// subdocument while {"address": {...}} replaces it.
func (c *Collection) Patch(ctx context.Context, id string, changes Document) (Document, error) {
	var result Document
	return result, c.client.do(ctx, http.MethodPatch, c.path("/documents/"+url.PathEscape(id)), nil, changes, &result)
}

// Delete removes a document and reports whether one was there.
func (c *Collection) Delete(ctx context.Context, id string) (bool, error) {
	err := c.client.do(ctx, http.MethodDelete, c.path("/documents/"+url.PathEscape(id)), nil, nil, nil)
	if IsNotFound(err) {
		return false, nil
	}
	return err == nil, err
}

// FindOptions narrows and pages a Find.
type FindOptions struct {
	// Filter is a CuteQL WHERE expression, such as: address.city = 'Bandung' AND total > 500000
	Filter string
	// Limit caps the returned rows. Zero means the server default of 100.
	Limit int
	// Offset skips rows, for paging.
	Offset int
}

// Find pages through the collection, optionally filtered.
func (c *Collection) Find(ctx context.Context, options FindOptions) (QueryResult, error) {
	query := url.Values{}
	if options.Filter != "" {
		query.Set("filter", options.Filter)
	}
	if options.Limit > 0 {
		query.Set("limit", strconv.Itoa(options.Limit))
	}
	if options.Offset > 0 {
		query.Set("offset", strconv.Itoa(options.Offset))
	}

	var result QueryResult
	return result, c.client.do(ctx, http.MethodGet, c.path("/documents"), query, nil, &result)
}

// Count returns how many documents match a CuteQL filter, or the total when filter is empty.
func (c *Collection) Count(ctx context.Context, filter string) (int, error) {
	where := ""
	if filter != "" {
		where = " WHERE " + filter
	}

	result, err := c.client.Query(ctx, "SELECT COUNT(*) AS n FROM "+c.name+where, nil)
	if err != nil {
		return 0, err
	}

	if count, ok := result.Scalar().(float64); ok {
		return int(count), nil
	}
	return 0, nil
}

// Stats returns this collection's size and indexes.
func (c *Collection) Stats(ctx context.Context) (CollectionStats, error) {
	var result CollectionStats
	return result, c.client.do(ctx, http.MethodGet, c.path(""), nil, nil, &result)
}

// CreateIndex adds a secondary index over a document path, such as "address.city" or "tags".
func (c *Collection) CreateIndex(ctx context.Context, path, name string, unique bool) (IndexInfo, error) {
	body := map[string]any{"path": path, "unique": unique}
	if name != "" {
		body["name"] = name
	}

	var result IndexInfo
	return result, c.client.do(ctx, http.MethodPost, c.path("/indexes"), nil, body, &result)
}

// DropIndex removes an index and reports whether one was there.
func (c *Collection) DropIndex(ctx context.Context, name string) (bool, error) {
	err := c.client.do(ctx, http.MethodDelete, c.path("/indexes/"+url.PathEscape(name)), nil, nil, nil)
	if IsNotFound(err) {
		return false, nil
	}
	return err == nil, err
}

// Decode unmarshals a document into a struct, using the usual encoding/json tags.
//
// Useful when a collection's shape is known even though the database does not enforce one. It
// round-trips through JSON rather than reflecting over the map, so json tags, embedded structs and
// custom UnmarshalJSON all behave exactly as they would anywhere else.
func Decode(document Document, target any) error {
	encoded, err := json.Marshal(document)
	if err != nil {
		return fmt.Errorf("cutedb: encoding document: %w", err)
	}

	if err := json.Unmarshal(encoded, target); err != nil {
		return fmt.Errorf("cutedb: decoding document: %w", err)
	}

	return nil
}

func (c *Client) do(ctx context.Context, method, path string, query url.Values, body, out any) error {
	target := c.baseURL + path
	if len(query) > 0 {
		target += "?" + query.Encode()
	}

	var payload io.Reader
	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			return fmt.Errorf("cutedb: encoding request: %w", err)
		}
		payload = bytes.NewReader(encoded)
	}

	request, err := http.NewRequestWithContext(ctx, method, target, payload)
	if err != nil {
		return fmt.Errorf("cutedb: building request: %w", err)
	}

	request.Header.Set("Accept", "application/json")
	if body != nil {
		request.Header.Set("Content-Type", "application/json; charset=utf-8")
	}
	if c.apiKey != "" {
		request.Header.Set("X-API-Key", c.apiKey)
	}

	response, err := c.http.Do(request)
	if err != nil {
		return fmt.Errorf("cutedb: %s %s: %w", method, path, err)
	}
	defer response.Body.Close()

	// Bounded: a runaway or hostile server should not be able to exhaust the client's memory.
	// 256 MiB is far above any plausible response and far below anything that matters.
	content, err := io.ReadAll(io.LimitReader(response.Body, 256<<20))
	if err != nil {
		return fmt.Errorf("cutedb: reading response: %w", err)
	}

	if response.StatusCode >= 400 {
		return toError(response.StatusCode, content)
	}

	if out == nil || len(content) == 0 {
		return nil
	}

	if err := json.Unmarshal(content, out); err != nil {
		return fmt.Errorf("cutedb: decoding response: %w", err)
	}

	return nil
}

func toError(status int, body []byte) error {
	apiError := &Error{Status: status, Message: http.StatusText(status)}

	var payload struct {
		Error   string `json:"error"`
		Message string `json:"message"`
	}

	// The server always sends JSON for its own failures. A body that is not JSON means something
	// else answered — a proxy, usually — and the status is all there is to go on.
	if err := json.Unmarshal(body, &payload); err == nil && payload.Message != "" {
		apiError.Code = payload.Error
		apiError.Message = payload.Message
	}

	return apiError
}
