package cutedb

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// newTestServer stands in for cutedb-server, so these tests exercise the client's own behaviour —
// request shape, error mapping, optional-404 handling — without needing a database or a build of
// the .NET server. The end-to-end check against a real server lives in CI.
func newTestServer(t *testing.T, handler http.HandlerFunc) (*Client, *httptest.Server) {
	t.Helper()

	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)

	return New(server.URL), server
}

func TestQuerySendsParametersAndDecodesResult(t *testing.T) {
	var received map[string]any

	client, _ := newTestServer(t, func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/query" {
			t.Errorf("path = %q, want /v1/query", r.URL.Path)
		}
		if r.Method != http.MethodPost {
			t.Errorf("method = %q, want POST", r.Method)
		}

		if err := json.NewDecoder(r.Body).Decode(&received); err != nil {
			t.Fatalf("decoding request: %v", err)
		}

		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{
			"kind": "select",
			"columns": ["city", "revenue"],
			"rows": [{"city": "Bandung", "revenue": 7214470861.10}],
			"affected": 1,
			"durationMs": 12.5,
			"plan": "Index seek on 'orders_city'"
		}`))
	})

	result, err := client.Query(
		context.Background(),
		"SELECT address.city AS city, SUM(total) AS revenue FROM orders WHERE address.city = @c GROUP BY address.city",
		map[string]any{"c": "Bandung"},
	)
	if err != nil {
		t.Fatalf("Query: %v", err)
	}

	if got := received["parameters"].(map[string]any)["c"]; got != "Bandung" {
		t.Errorf("parameter c = %v, want Bandung", got)
	}

	if len(result.Rows) != 1 || result.Rows[0]["city"] != "Bandung" {
		t.Errorf("rows = %v", result.Rows)
	}

	if result.Columns[0] != "city" {
		t.Errorf("columns = %v", result.Columns)
	}

	if !strings.Contains(result.Plan, "Index seek") {
		t.Errorf("plan = %q", result.Plan)
	}
}

func TestScalarReturnsFirstColumnOfFirstRow(t *testing.T) {
	result := QueryResult{Columns: []string{"n"}, Rows: []Document{{"n": float64(42)}}}
	if got := result.Scalar(); got != float64(42) {
		t.Errorf("Scalar() = %v, want 42", got)
	}

	empty := QueryResult{}
	if got := empty.Scalar(); got != nil {
		t.Errorf("Scalar() on an empty result = %v, want nil", got)
	}
}

func TestQueryErrorCarriesCodeAndMessage(t *testing.T) {
	client, _ := newTestServer(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(http.StatusBadRequest)
		_, _ = w.Write([]byte(`{"error":"invalid_query","message":"'~' does not belong in a query."}`))
	})

	_, err := client.Query(context.Background(), "SELECT * FROM orders WHERE total ~ 5", nil)
	if err == nil {
		t.Fatal("expected an error")
	}

	if !IsQueryError(err) {
		t.Errorf("IsQueryError = false for %v", err)
	}

	if !strings.Contains(err.Error(), "does not belong") {
		t.Errorf("error message lost the server's text: %v", err)
	}
}

func TestMissingDocumentIsNotAnError(t *testing.T) {
	client, _ := newTestServer(t, func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusNotFound)
		_, _ = w.Write([]byte(`{"error":"not_found","message":"No document."}`))
	})

	document, err := client.Collection("orders").Get(context.Background(), "0123456789abcdef01234567")
	if err != nil {
		t.Fatalf("Get on a missing document returned an error: %v", err)
	}
	if document != nil {
		t.Errorf("document = %v, want nil", document)
	}

	deleted, err := client.Collection("orders").Delete(context.Background(), "0123456789abcdef01234567")
	if err != nil {
		t.Fatalf("Delete on a missing document returned an error: %v", err)
	}
	if deleted {
		t.Error("Delete reported true for a document that was not there")
	}
}

func TestInsertManySendsOneRequest(t *testing.T) {
	calls := 0

	client, _ := newTestServer(t, func(w http.ResponseWriter, r *http.Request) {
		calls++

		var body []Document
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Fatalf("expected an array body: %v", err)
		}
		if len(body) != 3 {
			t.Errorf("received %d documents, want 3", len(body))
		}

		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"inserted":3,"ids":["a","b","c"]}`))
	})

	ids, err := client.Collection("notes").InsertMany(context.Background(), []Document{
		{"n": 1}, {"n": 2}, {"n": 3},
	})
	if err != nil {
		t.Fatalf("InsertMany: %v", err)
	}

	if calls != 1 {
		t.Errorf("made %d requests, want 1 — batching is the whole point", calls)
	}
	if len(ids) != 3 {
		t.Errorf("ids = %v", ids)
	}
}

func TestInsertManyWithNothingMakesNoRequest(t *testing.T) {
	client, _ := newTestServer(t, func(_ http.ResponseWriter, _ *http.Request) {
		t.Error("an empty batch should not reach the server")
	})

	ids, err := client.Collection("notes").InsertMany(context.Background(), nil)
	if err != nil || len(ids) != 0 {
		t.Errorf("InsertMany(nil) = %v, %v", ids, err)
	}
}

func TestAPIKeyIsSent(t *testing.T) {
	var presented string

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		presented = r.Header.Get("X-API-Key")
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"status":"ok"}`))
	}))
	defer server.Close()

	client := New(server.URL, WithAPIKey("rahasia"))
	if _, err := client.Health(context.Background()); err != nil {
		t.Fatalf("Health: %v", err)
	}

	if presented != "rahasia" {
		t.Errorf("X-API-Key = %q, want rahasia", presented)
	}
}

func TestFindBuildsQueryString(t *testing.T) {
	var query string

	client, _ := newTestServer(t, func(w http.ResponseWriter, r *http.Request) {
		query = r.URL.RawQuery
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"kind":"select","columns":[],"rows":[],"affected":0}`))
	})

	_, err := client.Collection("orders").Find(context.Background(), FindOptions{
		Filter: "total > 500000",
		Limit:  25,
		Offset: 50,
	})
	if err != nil {
		t.Fatalf("Find: %v", err)
	}

	for _, want := range []string{"filter=total", "limit=25", "offset=50"} {
		if !strings.Contains(query, want) {
			t.Errorf("query %q is missing %q", query, want)
		}
	}
}

func TestDecodeIntoStruct(t *testing.T) {
	type order struct {
		Code  string  `json:"code"`
		Total float64 `json:"total"`
		Lines []struct {
			SKU string `json:"sku"`
		} `json:"lines"`
	}

	document := Document{
		"code":  "SO-001",
		"total": 249000.0,
		"lines": []any{map[string]any{"sku": "KB-01"}},
	}

	var decoded order
	if err := Decode(document, &decoded); err != nil {
		t.Fatalf("Decode: %v", err)
	}

	if decoded.Code != "SO-001" || decoded.Total != 249000 || decoded.Lines[0].SKU != "KB-01" {
		t.Errorf("decoded = %+v", decoded)
	}
}

func TestDocumentID(t *testing.T) {
	if got := (Document{"_id": "abc"}).ID(); got != "abc" {
		t.Errorf("ID() = %q", got)
	}
	if got := (Document{}).ID(); got != "" {
		t.Errorf("ID() on a document without one = %q, want empty", got)
	}
}

func TestUnreachableServerIsReported(t *testing.T) {
	// Port 1 on loopback refuses connections everywhere this runs.
	client := New("http://127.0.0.1:1")

	if _, err := client.Health(context.Background()); err == nil {
		t.Fatal("expected a transport error")
	}
}
