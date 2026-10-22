package cutedb

import (
	"context"
	"encoding/json"
	"errors"
	"math"
	"net/http"
	"os"
	"reflect"
	"sort"
	"strings"
	"testing"
	"time"
)

// The golden cases are shared with the Python and Node builders through
// clients/querybuilder-golden.json; the .NET suite parses and runs the same file, so agreeing with
// it means agreeing with the real parser too.
//
// Set CUTEDB_TEST_SERVER (e.g. http://127.0.0.1:8420) to also run every golden case end to end
// against a live server. That test drops and recreates the qb_golden collection.

type goldenCase struct {
	Name       string         `json:"name"`
	CuteQL     string         `json:"cuteql"`
	Parameters map[string]any `json:"parameters"`
	Codes      []string       `json:"codes"`
	Ordered    bool           `json:"ordered"`
}

type goldenFile struct {
	Collection string       `json:"collection"`
	Fixture    []Document   `json:"fixture"`
	Cases      []goldenCase `json:"cases"`
}

func loadGolden(t *testing.T) goldenFile {
	t.Helper()
	raw, err := os.ReadFile("../../querybuilder-golden.json")
	if err != nil {
		t.Fatalf("reading golden file: %v", err)
	}
	var golden goldenFile
	if err := json.Unmarshal(raw, &golden); err != nil {
		t.Fatalf("parsing golden file: %v", err)
	}
	return golden
}

func utc(year int, month time.Month, day int) time.Time {
	return time.Date(year, month, day, 0, 0, 0, 0, time.UTC)
}

// goldenBuilders builds every golden case the way a person would write it.
func goldenBuilders(c string) map[string]func() *Query {
	F := Field
	return map[string]func() *Query{
		"select_all": func() *Query { return From(c) },
		"eq_string":  func() *Query { return From(c).Where(F("address.city").Eq("Bandung")) },
		"ne_and_gt": func() *Query {
			return From(c).Where(F("status").Ne("cancelled"), F("total").Gt(200000))
		},
		"ge_and_le": func() *Query {
			return From(c).Where(F("total").Ge(1000)).Where(F("total").Le(125000.5))
		},
		"lt_or_ge": func() *Query {
			return From(c).Where(Or(F("total").Lt(2000), F("total").Ge(900000)))
		},
		"or_inside_and": func() *Query {
			return From(c).Where(F("address.city").Eq("Bandung"), Or(F("status").Eq("cancelled"), F("total").Lt(2000)))
		},
		"not": func() *Query { return From(c).Where(Not(F("status").Eq("paid"))) },
		"not_or_and_present": func() *Query {
			return From(c).Where(Not(Or(F("address.city").Eq("Bandung"), F("address.city").Eq("Jakarta"))), F("total").IsNotMissing())
		},
		"in_strings": func() *Query { return From(c).Where(F("status").In("shipped", "draft")) },
		"in_numbers": func() *Query { return From(c).Where(F("total").In([]int{1000, 3000, 7})) },
		"not_in": func() *Query {
			return From(c).Where(F("address.city").NotIn("Bandung", "Jakarta"))
		},
		"between": func() *Query { return From(c).Where(F("total").Between(100000, 300000)) },
		"between_dates": func() *Query {
			return From(c).Where(F("placedAt").Between(utc(2026, 2, 1), utc(2026, 3, 31)))
		},
		"not_between":         func() *Query { return From(c).Where(F("total").NotBetween(1000, 900000)) },
		"is_null":             func() *Query { return From(c).Where(F("note").IsNull()) },
		"ne_none_is_not_null": func() *Query { return From(c).Where(F("note").Ne(nil)) },
		"is_missing":          func() *Query { return From(c).Where(F("total").IsMissing()) },
		"is_not_missing":      func() *Query { return From(c).Where(F("note").IsNotMissing()) },
		"nested_path":         func() *Query { return From(c).Where(F("customer.tier").Eq("gold")) },
		"projecting_path":     func() *Query { return From(c).Where(F("lines[].sku").Eq("NR-02")) },
		"indexed_path":        func() *Query { return From(c).Where(F("lines[0].qty").Gt(3)) },
		"array_elementwise":   func() *Query { return From(c).Where(F("tags").Eq("promo")) },
		"array_whole":         func() *Query { return From(c).Where(F("tags").Eq([]string{"promo", "bulk"})) },
		"bool_or_decimal": func() *Query {
			return From(c).Where(Or(F("vip").Eq(true), F("total").Eq(json.Number("125000.5"))))
		},
		"startswith_escapes_percent":  func() *Query { return From(c).Where(F("code").StartsWith("100%")) },
		"contains_escapes_underscore": func() *Query { return From(c).Where(F("code").Contains("%_")) },
		"endswith":                    func() *Query { return From(c).Where(F("customer.name").EndsWith("--")) },
		"like_raw_pattern":            func() *Query { return From(c).Where(F("code").Like("O_")) },
		"not_like":                    func() *Query { return From(c).Where(F("code").NotLike("1%")) },
		"injection_or_comment":        func() *Query { return From(c).Where(F("customer.name").Eq("' OR 1=1 --")) },
		"injection_quote":             func() *Query { return From(c).Where(F("customer.name").Eq("O'Brien")) },
		"injection_backslash": func() *Query {
			return From(c).Where(F("customer.name").Eq(`Back\slash "quoted"`))
		},
		"injection_no_match": func() *Query { return From(c).Where(F("code").Eq("x' OR '1'='1")) },
		"injection_startswith": func() *Query {
			return From(c).Where(F("customer.name").StartsWith("' OR 1=1 --"))
		},
		"unicode_and_newline": func() *Query {
			return From(c).Where(And(F("note").Eq("line1\nline2"), F("customer.name").Eq("Dewi Ünal 李")))
		},
		"projection_order_limit_offset": func() *Query {
			return From(c).Select("code").SelectAs("customer.name", "buyer").Select("total").
				Where(F("status").Eq("paid")).OrderByDesc("total").Limit(2).Offset(1)
		},
		"order_by_two_keys": func() *Query {
			return From(c).Where(F("address.city").Eq("Bandung")).OrderBy("status").OrderByDesc("total")
		},
		"order_limit": func() *Query { return From(c).Select("code").OrderBy("code").Limit(3) },
		"offset_only": func() *Query { return From(c).OrderBy("code").Offset(7) },
		"select_projecting_path": func() *Query {
			return From(c).Select("code").SelectAs("lines[].sku", "skus").Where(F("lines[].sku").Eq("NR-01"))
		},
	}
}

// asJSON is what the server will receive: the parameters after encoding/json.
func asJSON(t *testing.T, value any) any {
	t.Helper()
	encoded, err := json.Marshal(value)
	if err != nil {
		t.Fatalf("encoding: %v", err)
	}
	var decoded any
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatalf("decoding: %v", err)
	}
	return decoded
}

func TestGoldenCases(t *testing.T) {
	golden := loadGolden(t)
	builders := goldenBuilders(golden.Collection)

	if len(builders) != len(golden.Cases) {
		t.Errorf("%d builders for %d golden cases", len(builders), len(golden.Cases))
	}

	for _, item := range golden.Cases {
		t.Run(item.Name, func(t *testing.T) {
			build, ok := builders[item.Name]
			if !ok {
				t.Fatalf("no builder for golden case %q", item.Name)
			}

			text, parameters, err := build().Build()
			if err != nil {
				t.Fatalf("Build: %v", err)
			}
			if text != item.CuteQL {
				t.Errorf("text\n got: %s\nwant: %s", text, item.CuteQL)
			}
			if got, want := asJSON(t, parameters), asJSON(t, item.Parameters); !reflect.DeepEqual(got, want) {
				t.Errorf("parameters\n got: %v\nwant: %v", got, want)
			}
		})
	}
}

var hostileValues = []string{
	"' OR 1=1 --",
	"x'; DELETE FROM orders --",
	"''",
	`\'`,
	`" OR ""="`,
	"/* comment */",
	"@p1",
	"line\nWHERE 1=1",
	"\x00  Ünal 李 🙂",
}

func TestValuesNeverReachTheText(t *testing.T) {
	for _, hostile := range hostileValues {
		text, parameters, err := From("orders").Where(
			Field("name").Eq(hostile),
			Field("name").StartsWith(hostile),
			Field("tags").In(hostile),
			Field("name").Between(hostile, hostile),
		).Build()
		if err != nil {
			t.Fatalf("Build: %v", err)
		}

		want := "SELECT * FROM orders WHERE name = @p0 AND name LIKE @p1 AND tags IN @p2 AND name BETWEEN @p3 AND @p4"
		if text != want {
			t.Errorf("%q: text = %s", hostile, text)
		}
		if parameters["p0"] != hostile {
			t.Errorf("%q: p0 = %v", hostile, parameters["p0"])
		}
		if list, _ := parameters["p2"].([]any); len(list) != 1 || list[0] != hostile {
			t.Errorf("%q: p2 = %v", hostile, parameters["p2"])
		}
	}
}

func TestStartsWithEscapesLikeTheLinqProvider(t *testing.T) {
	// The same three replacements as CuteExpressionTranslator.Like, backslash first.
	if got := EscapeLike(`a\b%c_d`); got != `a\\b\%c\_d` {
		t.Errorf("EscapeLike = %s", got)
	}
	_, parameters, _ := From("t").Where(Field("x").StartsWith(`50%_\`)).Build()
	if parameters["p0"] != `50\%\_\\%` {
		t.Errorf("StartsWith pattern = %v", parameters["p0"])
	}
}

func TestHostileFieldNamesAreRefused(t *testing.T) {
	for _, bad := range []string{
		"name = 'x' OR 1=1 --", "name;", "name)", "a b", "a.", ".a", "a..b", "1abc", "a.1b",
		"lines[x]", "lines[0", "lines[ 0 ]", "lines[1234567890]", "a'b", `a"b`, "a-b", "a\nb",
		"kota_é", "@p0", "", "order", "COUNT", "Missing",
	} {
		_, _, err := From("t").Where(Field(bad).Eq(1)).Build()
		if !errors.Is(err, ErrInvalidQuery) {
			t.Errorf("Field(%q) was accepted", bad)
		}
	}

	for _, good := range []string{"a", "_x", "customer.address.city", "lines[0].sku", "lines[].sku", "m[-1]", "x.order", "count[0]"} {
		if _, _, err := From("t").Where(Field(good).Eq(1)).Build(); err != nil {
			t.Errorf("Field(%q): %v", good, err)
		}
	}
}

func TestNamesAreChecked(t *testing.T) {
	for _, q := range []*Query{
		From("orders; DROP"),
		From("a.b"),
		From("select"),
		From("t").SelectAs("x", "as"),
		From("t").Select("a; DELETE FROM t"),
		From("t").OrderBy("total DESC"),
		From("t").Limit(-1),
		From("t").Offset(math.MaxInt32 + 1),
	} {
		if _, _, err := q.Build(); !errors.Is(err, ErrInvalidQuery) {
			t.Errorf("accepted: %v", err)
		}
	}
}

func TestUnsupportedValuesAreRefused(t *testing.T) {
	for _, bad := range []any{
		struct{}{}, map[string]any{"a": 1}, []byte("x"), math.NaN(), math.Inf(1), uint64(math.MaxUint64),
		json.Number("1e"), make(chan int),
	} {
		if _, _, err := From("t").Where(Field("x").Eq(bad)).Build(); !errors.Is(err, ErrInvalidQuery) {
			t.Errorf("%T %v was accepted", bad, bad)
		}
	}
}

func TestNullComparisons(t *testing.T) {
	if text, _ := From("t").Where(Field("x").Eq(nil)).ToCuteQL(); text != "SELECT * FROM t WHERE x IS NULL" {
		t.Errorf("Eq(nil) = %s", text)
	}
	for _, c := range []Condition{Field("x").Gt(nil), Field("x").Between(nil, 1), And(), Or(), Not(nil)} {
		if _, _, err := From("t").Where(c).Build(); !errors.Is(err, ErrInvalidQuery) {
			t.Errorf("accepted: %v", err)
		}
	}
}

func TestDatesAreTaggedInUTCWithMilliseconds(t *testing.T) {
	wib := time.FixedZone("WIB", 7*3600)
	_, parameters, _ := From("t").Where(Field("t").Eq(time.Date(2026, 2, 1, 7, 0, 0, 123456789, wib))).Build()
	want := map[string]any{"$date": "2026-02-01T00:00:00.123Z"}
	if !reflect.DeepEqual(parameters["p0"], want) {
		t.Errorf("p0 = %v", parameters["p0"])
	}
}

func TestSameOperatorFlattensAndMixedOperatorsBracket(t *testing.T) {
	a, b, c, d := Field("a").Eq(1), Field("b").Eq(1), Field("c").Eq(1), Field("d").Eq(1)
	text, _ := From("t").Where(And(And(a, b), Or(c, Or(d, a)))).ToCuteQL()
	if want := "SELECT * FROM t WHERE a = @p0 AND b = @p1 AND (c = @p2 OR d = @p3 OR a = @p4)"; text != want {
		t.Errorf("got %s", text)
	}
	text, _ = From("t").Where(Not(Not(a))).ToCuteQL()
	if want := "SELECT * FROM t WHERE NOT (NOT (a = @p0))"; text != want {
		t.Errorf("got %s", text)
	}
}

func TestRunSendsTextAndParameters(t *testing.T) {
	var received map[string]any
	client, _ := newTestServer(t, func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewDecoder(r.Body).Decode(&received)
		_, _ = w.Write([]byte(`{"kind":"select","rows":[]}`))
	})

	_, err := client.Collection("orders").Query().Where(Field("city").Eq("' OR 1=1 --")).Limit(5).Run(context.Background())
	if err != nil {
		t.Fatalf("Run: %v", err)
	}
	if received["query"] != "SELECT * FROM orders WHERE city = @p0 LIMIT 5" {
		t.Errorf("query = %v", received["query"])
	}
	if received["parameters"].(map[string]any)["p0"] != "' OR 1=1 --" {
		t.Errorf("parameters = %v", received["parameters"])
	}

	if _, err := From("t").Run(context.Background()); !errors.Is(err, ErrInvalidQuery) {
		t.Errorf("an unbound query ran: %v", err)
	}
}

func TestLiveGoldenCases(t *testing.T) {
	server := os.Getenv("CUTEDB_TEST_SERVER")
	if server == "" {
		t.Skip("set CUTEDB_TEST_SERVER to run against a live server")
	}

	ctx := context.Background()
	golden := loadGolden(t)
	client := New(server, WithAPIKey(os.Getenv("CUTEDB_API_KEY")))
	if _, err := client.DropCollection(ctx, golden.Collection); err != nil {
		t.Fatalf("DropCollection: %v", err)
	}
	if _, err := client.Collection(golden.Collection).InsertMany(ctx, golden.Fixture); err != nil {
		t.Fatalf("InsertMany: %v", err)
	}

	builders := goldenBuilders(golden.Collection)
	for _, item := range golden.Cases {
		t.Run(item.Name, func(t *testing.T) {
			result, err := client.Run(ctx, builders[item.Name]())
			if err != nil {
				t.Fatalf("Run: %v", err)
			}
			codes := []string{}
			for _, row := range result.Rows {
				codes = append(codes, row["code"].(string))
			}
			want := append([]string{}, item.Codes...)
			if !item.Ordered {
				sort.Strings(codes)
				sort.Strings(want)
			}
			if strings.Join(codes, "|") != strings.Join(want, "|") {
				t.Errorf("codes = %v, want %v", codes, want)
			}
		})
	}
}
