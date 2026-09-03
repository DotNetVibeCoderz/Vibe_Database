package memsharp_test

// Integration tests for the Go client.
//
// These run against a live server rather than a mock, because the thing worth testing is that the
// wire encoding matches what MemSharp actually sends back. Start one first:
//
//	memsharp serve --port 6391 --quiet
//	go test ./clients/go/...
//
// Without a server the tests skip rather than fail, so `go test ./...` on a fresh checkout stays
// green.

import (
	"os"
	"sort"
	"testing"
	"time"

	memsharp "github.com/DotNetVibeCoderz/Vibe_Database/MemSharp/clients/go"
)

func address() string {
	if value := os.Getenv("MEMSHARP_TEST_ADDRESS"); value != "" {
		return value
	}
	return "127.0.0.1:6391"
}

// connect dials the test server, skipping the test when none is running.
func connect(t *testing.T) *memsharp.Client {
	t.Helper()

	db, err := memsharp.Dial(address())
	if err != nil {
		t.Skipf("no MemSharp server at %s: %v", address(), err)
	}
	t.Cleanup(func() { _ = db.Close() })
	return db
}

func TestStrings(t *testing.T) {
	db := connect(t)

	if err := db.Set("go:k", "v"); err != nil {
		t.Fatalf("Set: %v", err)
	}

	value, found, err := db.Get("go:k")
	if err != nil || !found || value != "v" {
		t.Fatalf("Get = %q, %v, %v; want \"v\", true, nil", value, found, err)
	}

	if _, found, _ := db.Get("go:absent"); found {
		t.Error("Get on a missing key reported found")
	}

	if n, err := db.Incr("go:n", 41); err != nil || n != 41 {
		t.Fatalf("Incr = %d, %v; want 41", n, err)
	}
	if n, err := db.Incr("go:n", 1); err != nil || n != 42 {
		t.Fatalf("Incr = %d, %v; want 42", n, err)
	}

	if f, err := db.IncrByFloat("go:f", 1.5); err != nil || f != 1.5 {
		t.Fatalf("IncrByFloat = %v, %v; want 1.5", f, err)
	}

	_, _ = db.Del("go:k", "go:n", "go:f")
}

func TestMGetPreservesMissingKeys(t *testing.T) {
	db := connect(t)

	_ = db.Set("go:a", "1")
	_ = db.Set("go:c", "3")
	defer db.Del("go:a", "go:c")

	values, err := db.MGet("go:a", "go:b", "go:c")
	if err != nil {
		t.Fatalf("MGet: %v", err)
	}
	if len(values) != 3 {
		t.Fatalf("MGet returned %d values; want 3", len(values))
	}

	// A nil pointer rather than an empty string, so a missing key stays distinguishable from a key
	// holding "".
	if values[0] == nil || *values[0] != "1" {
		t.Errorf("values[0] = %v; want \"1\"", values[0])
	}
	if values[1] != nil {
		t.Errorf("values[1] = %v; want nil for the missing key", *values[1])
	}
	if values[2] == nil || *values[2] != "3" {
		t.Errorf("values[2] = %v; want \"3\"", values[2])
	}
}

func TestExpiry(t *testing.T) {
	db := connect(t)

	if err := db.SetEx("go:temp", "v", time.Minute); err != nil {
		t.Fatalf("SetEx: %v", err)
	}

	ttl, hasTTL, err := db.TTL("go:temp")
	if err != nil || !hasTTL || ttl > time.Minute || ttl < 55*time.Second {
		t.Fatalf("TTL = %v, %v, %v; want about a minute", ttl, hasTTL, err)
	}

	_ = db.Set("go:permanent", "v")
	if _, hasTTL, _ := db.TTL("go:permanent"); hasTTL {
		t.Error("a permanent key reported a TTL")
	}

	if cleared, err := db.Persist("go:temp"); err != nil || !cleared {
		t.Fatalf("Persist = %v, %v; want true", cleared, err)
	}

	_ = db.SetEx("go:brief", "v", 50*time.Millisecond)
	time.Sleep(200 * time.Millisecond)
	if _, found, _ := db.Get("go:brief"); found {
		t.Error("an expired key was still readable")
	}

	_, _ = db.Del("go:temp", "go:permanent")
}

func TestSetNX(t *testing.T) {
	db := connect(t)
	defer db.Del("go:nx")

	if stored, err := db.SetNX("go:nx", "first"); err != nil || !stored {
		t.Fatalf("SetNX on an absent key = %v, %v; want true", stored, err)
	}
	if stored, err := db.SetNX("go:nx", "second"); err != nil || stored {
		t.Fatalf("SetNX on an existing key = %v, %v; want false", stored, err)
	}

	value, _, _ := db.Get("go:nx")
	if value != "first" {
		t.Errorf("value = %q; want \"first\"", value)
	}
}

func TestLists(t *testing.T) {
	db := connect(t)
	defer db.Del("go:list")

	if n, err := db.RPush("go:list", "a", "b", "c"); err != nil || n != 3 {
		t.Fatalf("RPush = %d, %v; want 3", n, err)
	}

	items, err := db.LRange("go:list", 0, -1)
	if err != nil || len(items) != 3 || items[0] != "a" || items[2] != "c" {
		t.Fatalf("LRange = %v, %v; want [a b c]", items, err)
	}

	// Negative indices count back from the end.
	tail, err := db.LRange("go:list", -2, -1)
	if err != nil || len(tail) != 2 || tail[0] != "b" {
		t.Fatalf("LRange(-2, -1) = %v, %v; want [b c]", tail, err)
	}

	if _, _ = db.LPush("go:list", "z"); true {
		if head, found, _ := db.LPop("go:list"); !found || head != "z" {
			t.Errorf("LPop = %q, %v; want \"z\", true", head, found)
		}
	}

	if last, found, _ := db.RPop("go:list"); !found || last != "c" {
		t.Errorf("RPop = %q, %v; want \"c\", true", last, found)
	}

	if err := db.LTrim("go:list", 0, 0); err != nil {
		t.Fatalf("LTrim: %v", err)
	}
	if n, _ := db.LLen("go:list"); n != 1 {
		t.Errorf("LLen after LTrim = %d; want 1", n)
	}
}

func TestHashes(t *testing.T) {
	db := connect(t)
	defer db.Del("go:hash")

	if _, err := db.HSet("go:hash", map[string]string{"name": "Kang Fadhil", "desk": "Jakarta"}); err != nil {
		t.Fatalf("HSet: %v", err)
	}

	fields, err := db.HGetAll("go:hash")
	if err != nil || fields["name"] != "Kang Fadhil" || fields["desk"] != "Jakarta" {
		t.Fatalf("HGetAll = %v, %v", fields, err)
	}

	if value, found, _ := db.HGet("go:hash", "desk"); !found || value != "Jakarta" {
		t.Errorf("HGet = %q, %v; want \"Jakarta\", true", value, found)
	}
	if _, found, _ := db.HGet("go:hash", "absent"); found {
		t.Error("HGet on a missing field reported found")
	}

	if n, err := db.HIncrBy("go:hash", "fills", 5); err != nil || n != 5 {
		t.Fatalf("HIncrBy = %d, %v; want 5", n, err)
	}
	if n, err := db.HDel("go:hash", "desk"); err != nil || n != 1 {
		t.Fatalf("HDel = %d, %v; want 1", n, err)
	}
}

func TestSets(t *testing.T) {
	db := connect(t)
	defer db.Del("go:set")

	if n, err := db.SAdd("go:set", "x", "y", "x"); err != nil || n != 2 {
		t.Fatalf("SAdd = %d, %v; want 2 - duplicates should not count", n, err)
	}

	members, err := db.SMembers("go:set")
	sort.Strings(members)
	if err != nil || len(members) != 2 || members[0] != "x" || members[1] != "y" {
		t.Fatalf("SMembers = %v, %v; want [x y]", members, err)
	}

	if has, _ := db.SIsMember("go:set", "x"); !has {
		t.Error("SIsMember said x is absent")
	}
	if has, _ := db.SIsMember("go:set", "q"); has {
		t.Error("SIsMember said q is present")
	}
	if n, _ := db.SRem("go:set", "x"); n != 1 {
		t.Errorf("SRem = %d; want 1", n)
	}
}

func TestSortedSets(t *testing.T) {
	db := connect(t)
	defer db.Del("go:zset")

	_, err := db.ZAdd("go:zset",
		memsharp.ScoredMember{Member: "low", Score: 1},
		memsharp.ScoredMember{Member: "high", Score: 3},
		memsharp.ScoredMember{Member: "mid", Score: 2})
	if err != nil {
		t.Fatalf("ZAdd: %v", err)
	}

	ascending, err := db.ZRange("go:zset", 0, -1, false)
	if err != nil || len(ascending) != 3 || ascending[0] != "low" || ascending[2] != "high" {
		t.Fatalf("ZRange = %v, %v; want [low mid high]", ascending, err)
	}

	descending, _ := db.ZRange("go:zset", 0, -1, true)
	if len(descending) != 3 || descending[0] != "high" {
		t.Fatalf("ZRange descending = %v; want [high mid low]", descending)
	}

	scored, err := db.ZRangeWithScores("go:zset", 0, 0, false)
	if err != nil || len(scored) != 1 || scored[0].Member != "low" || scored[0].Score != 1 {
		t.Fatalf("ZRangeWithScores = %v, %v", scored, err)
	}

	window, _ := db.ZRangeByScore("go:zset", 1.5, 2.5)
	if len(window) != 1 || window[0] != "mid" {
		t.Fatalf("ZRangeByScore = %v; want [mid]", window)
	}

	if score, found, _ := db.ZScore("go:zset", "high"); !found || score != 3 {
		t.Errorf("ZScore = %v, %v; want 3, true", score, found)
	}
	if _, found, _ := db.ZScore("go:zset", "absent"); found {
		t.Error("ZScore on a missing member reported found")
	}
	if n, _ := db.ZCard("go:zset"); n != 3 {
		t.Errorf("ZCard = %d; want 3", n)
	}
}

func TestStreams(t *testing.T) {
	db := connect(t)
	defer db.Del("go:stream", "go:capped")

	id, err := db.XAdd("go:stream", map[string]string{"sym": "BTC", "qty": "5"}, 0)
	if err != nil || id == "" {
		t.Fatalf("XAdd = %q, %v", id, err)
	}
	_, _ = db.XAdd("go:stream", map[string]string{"sym": "ETH", "qty": "12"}, 0)

	if n, _ := db.XLen("go:stream"); n != 2 {
		t.Fatalf("XLen = %d; want 2", n)
	}

	entries, err := db.XRange("go:stream", "-", "+", 0)
	if err != nil || len(entries) != 2 {
		t.Fatalf("XRange = %v, %v; want 2 entries", entries, err)
	}
	if entries[0].Fields["sym"] != "BTC" || entries[0].Fields["qty"] != "5" {
		t.Errorf("entries[0].Fields = %v", entries[0].Fields)
	}

	for i := 0; i < 50; i++ {
		_, _ = db.XAdd("go:capped", map[string]string{"n": "x"}, 10)
	}
	if n, _ := db.XLen("go:capped"); n != 10 {
		t.Errorf("XLen with MAXLEN 10 = %d; want 10", n)
	}
}

func TestTimeSeries(t *testing.T) {
	db := connect(t)

	// A fresh key each run: a series left over from a previous run holds a server-stamped sample,
	// and the small timestamps below would then be rejected as out of order - correctly.
	key := "go:ts"
	_, _ = db.Del(key)
	defer db.Del(key)

	if err := db.TSCreate(key, 1000); err != nil {
		t.Fatalf("TSCreate: %v", err)
	}

	for i := int64(0); i < 20; i++ {
		if _, err := db.TSAdd(key, float64(i), i*100); err != nil {
			t.Fatalf("TSAdd: %v", err)
		}
	}

	// TSAddNow uses the server clock, so it lands after every explicit timestamp above.
	if stamped, err := db.TSAddNow(key, 99); err != nil || stamped <= 1900 {
		t.Fatalf("TSAddNow = %d, %v; want a server timestamp", stamped, err)
	}

	samples, err := db.TSRange(key, 0, 10_000)
	if err != nil || len(samples) != 20 {
		t.Fatalf("TSRange = %d samples, %v; want 20", len(samples), err)
	}
	if samples[0].Timestamp != 0 || samples[0].Value != 0 {
		t.Errorf("samples[0] = %+v; want {0 0}", samples[0])
	}

	buckets, err := db.TSAggregate(key, 0, 10_000, 500, "max")
	if err != nil || len(buckets) != 4 {
		t.Fatalf("TSAggregate = %v, %v; want 4 buckets", buckets, err)
	}
	if buckets[0].Value != 4 {
		t.Errorf("buckets[0].Value = %v; want 4", buckets[0].Value)
	}
}

func TestSQL(t *testing.T) {
	db := connect(t)

	for i := 0; i < 20; i++ {
		_ = db.Set("go:order:"+string(rune('a'+i)), "value")
	}
	defer db.SQLDelete("DELETE FROM keys WHERE key LIKE 'go:order:%'")

	rows, err := db.SQL("SELECT key, type FROM keys WHERE key LIKE 'go:order:%' ORDER BY key LIMIT 5")
	if err != nil {
		t.Fatalf("SQL: %v", err)
	}
	if len(rows) != 5 {
		t.Fatalf("SQL returned %d rows; want 5", len(rows))
	}

	// Rows come back keyed by column name rather than by position.
	if rows[0]["type"] != "String" || rows[0]["key"] == "" {
		t.Errorf("rows[0] = %v; want key and type columns", rows[0])
	}

	removed, err := db.SQLDelete("DELETE FROM keys WHERE key LIKE 'go:order:%'")
	if err != nil || removed != 20 {
		t.Fatalf("SQLDelete = %d, %v; want 20", removed, err)
	}
}

func TestErrorsCarryTheirCode(t *testing.T) {
	db := connect(t)
	defer db.Del("go:errlist")

	_, _ = db.RPush("go:errlist", "x")

	_, _, err := db.Get("go:errlist")
	if err == nil {
		t.Fatal("Get on a list did not fail")
	}
	if !memsharp.IsWrongType(err) {
		t.Errorf("IsWrongType(%v) = false; want true", err)
	}

	// An error must not desynchronise the connection.
	if reply, err := db.Ping(); err != nil || reply != "PONG" {
		t.Errorf("Ping after an error = %q, %v; want PONG", reply, err)
	}
}

func TestPipeline(t *testing.T) {
	db := connect(t)

	commands := make([][]any, 0, 500)
	for i := 0; i < 500; i++ {
		commands = append(commands, []any{"SET", "go:p" + string(rune(i%26+'a')) + string(rune(i/26+'a')), i})
	}

	replies, err := db.Pipeline(commands)
	if err != nil {
		t.Fatalf("Pipeline: %v", err)
	}
	if len(replies) != len(commands) {
		t.Fatalf("Pipeline returned %d replies; want %d", len(replies), len(commands))
	}
	for i, reply := range replies {
		if reply != "OK" {
			t.Fatalf("replies[%d] = %v; want OK", i, reply)
		}
	}

	// Errors come back in place rather than aborting the batch.
	mixed, err := db.Pipeline([][]any{{"PING"}, {"NOSUCHCOMMAND"}, {"PING"}})
	if err != nil {
		t.Fatalf("Pipeline with a bad command: %v", err)
	}
	if mixed[0] != "PONG" || mixed[2] != "PONG" {
		t.Errorf("surrounding replies = %v, %v; want PONG", mixed[0], mixed[2])
	}
	if _, isError := mixed[1].(*memsharp.Error); !isError {
		t.Errorf("mixed[1] = %T; want *memsharp.Error", mixed[1])
	}

	_, _ = db.SQLDelete("DELETE FROM keys WHERE key LIKE 'go:p%'")
}

func TestScanVisitsEveryKey(t *testing.T) {
	db := connect(t)

	for i := 0; i < 200; i++ {
		_ = db.Set("go:scan:"+string(rune('a'+i%26))+string(rune('a'+i/26)), "v")
	}
	defer db.SQLDelete("DELETE FROM keys WHERE key LIKE 'go:scan:%'")

	seen := make(map[string]bool)
	err := db.Scan("go:scan:*", 32, func(key string) error {
		seen[key] = true
		return nil
	})
	if err != nil {
		t.Fatalf("Scan: %v", err)
	}
	if len(seen) != 200 {
		t.Errorf("Scan visited %d distinct keys; want 200", len(seen))
	}
}

func TestPubSub(t *testing.T) {
	db := connect(t)

	subscriber, err := memsharp.Dial(address())
	if err != nil {
		t.Skipf("no server: %v", err)
	}
	defer subscriber.Close()

	received := make(chan string, 4)
	done := make(chan struct{})

	go func() {
		defer close(done)
		_ = subscriber.Subscribe(func(message memsharp.Message) error {
			// The warmup publishes below only detect that the subscription registered.
			if message.Payload == "warmup" {
				return nil
			}
			received <- message.Payload
			if len(received) == 2 {
				return errStopSubscription
			}
			return nil
		}, "go:news")
	}()

	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if n, _ := db.Publish("go:news", "warmup"); n > 0 {
			break
		}
		time.Sleep(50 * time.Millisecond)
	}

	_, _ = db.Publish("go:news", "one")
	_, _ = db.Publish("go:news", "two")

	select {
	case <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("pub/sub messages did not arrive")
	}

	close(received)
	var payloads []string
	for payload := range received {
		payloads = append(payloads, payload)
	}
	if len(payloads) != 2 || payloads[0] != "one" || payloads[1] != "two" {
		t.Errorf("payloads = %v; want [one two]", payloads)
	}
}

// errStopSubscription ends a Subscribe loop from inside the callback.
var errStopSubscription = &memsharp.Error{Code: "STOP", Message: "test finished"}

func TestServerInfo(t *testing.T) {
	db := connect(t)

	info, err := db.Info()
	if err != nil {
		t.Fatalf("Info: %v", err)
	}
	if info["product"] != "MemSharp" {
		t.Errorf("info[product] = %q; want MemSharp", info["product"])
	}
	if _, present := info["keys"]; !present {
		t.Error("info has no keys entry")
	}

	if _, err := db.DBSize(); err != nil {
		t.Errorf("DBSize: %v", err)
	}
}
