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
	"errors"
	"fmt"
	"os"
	"sort"
	"strconv"
	"strings"
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

// -- 1.1 ---------------------------------------------------------------------------------

// replyCode returns the code of an *Error reply, or "" for any other error.
func replyCode(err error) string {
	var replyErr *memsharp.Error
	if errors.As(err, &replyErr) {
		return replyErr.Code
	}
	return ""
}

func TestServerInfoSections(t *testing.T) {
	db := connect(t)

	keyspace, err := db.Info("keyspace")
	if err != nil {
		t.Fatalf("Info(keyspace): %v", err)
	}
	if !strings.HasPrefix(keyspace["db0"], "keys=") {
		t.Errorf("keyspace[db0] = %q; want keys=...", keyspace["db0"])
	}
	if _, present := keyspace["redis_version"]; present {
		t.Error("Info(keyspace) returned the server section too")
	}

	two, err := db.Info("server", "memory")
	if err != nil || two["memsharp_version"] == "" || two["used_memory"] == "" {
		t.Errorf("Info(server, memory) = %v, %v; want memsharp_version and used_memory", two, err)
	}
}

func TestConfigAndMemory(t *testing.T) {
	db := connect(t)

	config, err := db.ConfigGet()
	if err != nil || config["maxmemory-policy"] != "noeviction" || config["shards"] == "" {
		t.Errorf("ConfigGet() = %v, %v", config, err)
	}

	some, err := db.ConfigGet("maxmemory", "nope")
	if err != nil || len(some) != 1 || some["maxmemory"] != "0" {
		t.Errorf("ConfigGet(maxmemory, nope) = %v, %v; want {maxmemory: 0}", some, err)
	}

	if _, err := db.Do("CONFIG", "SET", "maxmemory", "1"); err == nil {
		t.Error("CONFIG SET succeeded; it is deliberately unsupported")
	}
	if err := db.ConfigResetStat(); err != nil {
		t.Errorf("ConfigResetStat: %v", err)
	}

	_ = db.Set("go:mem", "value")
	_, _ = db.RPush("go:memlist", "a", "b", "c")
	defer db.Del("go:mem", "go:memlist")

	if bytes, found, err := db.MemoryUsage("go:mem", 0); err != nil || !found || bytes <= 0 {
		t.Errorf("MemoryUsage = %d, %v, %v; want a positive size", bytes, found, err)
	}
	if bytes, found, err := db.MemoryUsage("go:memlist", 2); err != nil || !found || bytes <= 0 {
		t.Errorf("MemoryUsage with samples = %d, %v, %v", bytes, found, err)
	}
	if _, found, err := db.MemoryUsage("go:absent", 0); err != nil || found {
		t.Errorf("MemoryUsage on a missing key: found = %v, err = %v", found, err)
	}
}

func TestByteRanges(t *testing.T) {
	db := connect(t)
	defer db.Del("go:utf", "go:pad")

	_ = db.Set("go:utf", "héllo")
	if n, err := db.StrLen("go:utf"); err != nil || n != 6 {
		t.Fatalf("StrLen(héllo) = %d, %v; want 6 bytes", n, err)
	}

	if s, err := db.GetRange("go:utf", 0, 2); err != nil || s != "hé" {
		t.Errorf("GetRange(0, 2) = %q, %v; want \"hé\"", s, err)
	}
	if s, err := db.GetRange("go:utf", -3, -1); err != nil || s != "llo" {
		t.Errorf("GetRange(-3, -1) = %q, %v; want \"llo\"", s, err)
	}
	if s, err := db.GetRange("go:absent", 0, 5); err != nil || s != "" {
		t.Errorf("GetRange on a missing key = %q, %v; want \"\"", s, err)
	}

	// Byte 1 is the first half of é, so 0..1 would return invalid UTF-8.
	if _, err := db.GetRange("go:utf", 0, 1); err == nil || !strings.Contains(err.Error(), "multi-byte") {
		t.Errorf("GetRange splitting é: err = %v; want a multi-byte error", err)
	}

	if n, err := db.SetRange("go:pad", 3, "ab"); err != nil || n != 5 {
		t.Errorf("SetRange = %d, %v; want 5", n, err)
	}
	if value, _, _ := db.Get("go:pad"); value != "\x00\x00\x00ab" {
		t.Errorf("SetRange padding = %q", value)
	}
	if n, err := db.SetRange("go:utf", 3, "LO"); err != nil || n != 6 {
		t.Errorf("SetRange overwrite = %d, %v; want 6", n, err)
	}
	if value, _, _ := db.Get("go:utf"); value != "héLOo" {
		t.Errorf("after SetRange = %q; want héLOo", value)
	}
}

func TestLPos(t *testing.T) {
	db := connect(t)
	defer db.Del("go:pos")

	_, _ = db.RPush("go:pos", "a", "b", "c", "b", "b")

	if i, found, err := db.LPos("go:pos", "b", memsharp.LPosOptions{}); err != nil || !found || i != 1 {
		t.Errorf("LPos = %d, %v, %v; want 1", i, found, err)
	}
	if _, found, err := db.LPos("go:pos", "q", memsharp.LPosOptions{}); err != nil || found {
		t.Errorf("LPos on a missing element: found = %v, err = %v", found, err)
	}
	if i, _, _ := db.LPos("go:pos", "b", memsharp.LPosOptions{Rank: -1}); i != 4 {
		t.Errorf("LPos rank -1 = %d; want 4", i)
	}
	if i, _, _ := db.LPos("go:pos", "b", memsharp.LPosOptions{Rank: 2}); i != 3 {
		t.Errorf("LPos rank 2 = %d; want 3", i)
	}

	if all, err := db.LPosCount("go:pos", "b", 0, memsharp.LPosOptions{}); err != nil || !equalInts(all, 1, 3, 4) {
		t.Errorf("LPosCount 0 = %v, %v; want [1 3 4]", all, err)
	}
	if two, _ := db.LPosCount("go:pos", "b", 2, memsharp.LPosOptions{}); !equalInts(two, 1, 3) {
		t.Errorf("LPosCount 2 = %v; want [1 3]", two)
	}
	if none, err := db.LPosCount("go:pos", "q", 1, memsharp.LPosOptions{}); err != nil || len(none) != 0 {
		t.Errorf("LPosCount on a missing element = %v, %v; want []", none, err)
	}
	if capped, _ := db.LPosCount("go:pos", "b", 0, memsharp.LPosOptions{MaxLen: 3}); !equalInts(capped, 1) {
		t.Errorf("LPosCount MaxLen 3 = %v; want [1]", capped)
	}
}

func equalInts(got []int64, want ...int64) bool {
	if len(got) != len(want) {
		return false
	}
	for i := range got {
		if got[i] != want[i] {
			return false
		}
	}
	return true
}

func TestStoreCommands(t *testing.T) {
	db := connect(t)
	defer db.Del("go:sa", "go:sb", "go:si", "go:su", "go:sd", "go:z1", "go:z2", "go:zu", "go:zw", "go:zi", "go:plain", "go:zp")

	_, _ = db.SAdd("go:sa", "1", "2", "3")
	_, _ = db.SAdd("go:sb", "2", "3", "4")

	if n, err := db.SInterStore("go:si", "go:sa", "go:sb"); err != nil || n != 2 {
		t.Errorf("SInterStore = %d, %v; want 2", n, err)
	}
	if n, _ := db.SUnionStore("go:su", "go:sa", "go:sb"); n != 4 {
		t.Errorf("SUnionStore = %d; want 4", n)
	}
	if n, _ := db.SDiffStore("go:sd", "go:sa", "go:sb"); n != 1 {
		t.Errorf("SDiffStore = %d; want 1", n)
	}
	if members, _ := db.SMembers("go:sd"); len(members) != 1 || members[0] != "1" {
		t.Errorf("SDiffStore stored %v; want [1]", members)
	}
	if n, _ := db.SInterStore("go:si", "go:sa", "go:absent"); n != 0 {
		t.Errorf("empty SInterStore = %d; want 0", n)
	}
	if n, _ := db.Exists("go:si"); n != 0 {
		t.Error("an empty store result left the destination behind")
	}

	_, _ = db.ZAdd("go:z1", memsharp.ScoredMember{Member: "x", Score: 1}, memsharp.ScoredMember{Member: "y", Score: 2})
	_, _ = db.ZAdd("go:z2", memsharp.ScoredMember{Member: "y", Score: 10}, memsharp.ScoredMember{Member: "w", Score: 20})

	if n, err := db.ZUnionStore("go:zu", []string{"go:z1", "go:z2"}, memsharp.ZStoreOptions{}); err != nil || n != 3 {
		t.Errorf("ZUnionStore = %d, %v; want 3", n, err)
	}
	if score, _, _ := db.ZScore("go:zu", "y"); score != 12 {
		t.Errorf("ZUnionStore sum of y = %v; want 12", score)
	}

	options := memsharp.ZStoreOptions{Weights: []float64{1, 2}, Aggregate: "max"}
	if n, _ := db.ZUnionStore("go:zw", []string{"go:z1", "go:z2"}, options); n != 3 {
		t.Errorf("weighted ZUnionStore = %d; want 3", n)
	}
	scored, _ := db.ZRangeWithScores("go:zw", 0, -1, false)
	want := []memsharp.ScoredMember{{Member: "x", Score: 1}, {Member: "y", Score: 20}, {Member: "w", Score: 40}}
	if len(scored) != 3 || scored[0] != want[0] || scored[1] != want[1] || scored[2] != want[2] {
		t.Errorf("weighted ZUnionStore = %v; want %v", scored, want)
	}

	if n, _ := db.ZInterStore("go:zi", []string{"go:z1", "go:z2"}, memsharp.ZStoreOptions{Aggregate: "min"}); n != 1 {
		t.Errorf("ZInterStore = %d; want 1", n)
	}
	if score, _, _ := db.ZScore("go:zi", "y"); score != 2 {
		t.Errorf("ZInterStore min of y = %v; want 2", score)
	}

	// A plain set counts as every member scoring 1.
	_, _ = db.SAdd("go:plain", "x")
	if n, _ := db.ZInterStore("go:zp", []string{"go:z1", "go:plain"}, memsharp.ZStoreOptions{}); n != 1 {
		t.Errorf("ZInterStore with a plain set = %d; want 1", n)
	}
	if score, _, _ := db.ZScore("go:zp", "x"); score != 2 {
		t.Errorf("ZInterStore plain-set score = %v; want 2", score)
	}
}

func TestScanCursorAbove32Bits(t *testing.T) {
	db := connect(t)

	for i := 0; i < 40; i++ {
		_ = db.Set(fmt.Sprintf("go:wide:%d", i), "v")
	}
	defer db.SQLDelete("DELETE FROM keys WHERE key LIKE 'go:wide:%'")

	// The shard index rides in the cursor's high 32 bits, so any cursor past the first shard is above
	// 2^32. Walk the raw cursors to prove one was, then that Scan survives it.
	var largest uint64
	cursor := "0"
	for {
		reply, err := db.Do("SCAN", cursor, "COUNT", 1)
		if err != nil {
			t.Fatalf("SCAN: %v", err)
		}
		cursor, _ = reply.([]any)[0].(string)
		value, err := strconv.ParseUint(cursor, 10, 64)
		if err != nil {
			t.Fatalf("cursor %q is not an unsigned integer: %v", cursor, err)
		}
		if value > largest {
			largest = value
		}
		if cursor == "0" {
			break
		}
	}
	if largest <= 1<<32 {
		t.Errorf("largest cursor = %d; want one above 2^32", largest)
	}

	seen := 0
	_ = db.Scan("go:wide:*", 1, func(string) error { seen++; return nil })
	if seen != 40 {
		t.Errorf("Scan with COUNT 1 visited %d keys; want 40", seen)
	}
}

func TestXRead(t *testing.T) {
	db := connect(t)
	_, _ = db.Del("go:rs")
	defer db.Del("go:rs")

	a, _ := db.XAdd("go:rs", map[string]string{"n": "1"}, 0)
	b, _ := db.XAdd("go:rs", map[string]string{"n": "2"}, 0)

	read, err := db.XRead(map[string]string{"go:rs": "0", "go:rs-absent": "0"}, memsharp.XReadOptions{})
	if err != nil || len(read) != 1 || len(read["go:rs"]) != 2 {
		t.Fatalf("XRead from 0 = %v, %v; want two entries from go:rs only", read, err)
	}
	if read["go:rs"][0].ID != a || read["go:rs"][0].Fields["n"] != "1" || read["go:rs"][1].ID != b {
		t.Errorf("XRead entries = %v", read["go:rs"])
	}

	if one, _ := db.XRead(map[string]string{"go:rs": "0"}, memsharp.XReadOptions{Count: 1}); len(one["go:rs"]) != 1 {
		t.Errorf("XRead Count 1 = %v", one)
	}
	if after, _ := db.XRead(map[string]string{"go:rs": a}, memsharp.XReadOptions{}); len(after["go:rs"]) != 1 || after["go:rs"][0].ID != b {
		t.Errorf("XRead after a = %v; want only b", after)
	}
	if none, err := db.XRead(map[string]string{"go:rs": ""}, memsharp.XReadOptions{}); err != nil || none != nil {
		t.Errorf("XRead $ = %v, %v; want nil", none, err)
	}

	started := time.Now()
	timedOut, err := db.XRead(map[string]string{"go:rs": "$"}, memsharp.XReadOptions{Block: 150 * time.Millisecond})
	if err != nil || timedOut != nil {
		t.Errorf("XRead block timeout = %v, %v; want nil", timedOut, err)
	}
	if waited := time.Since(started); waited < 100*time.Millisecond {
		t.Errorf("XRead block returned after %v; want about 150ms", waited)
	}

	// Parked forever: only an XADD from a second connection can end it.
	addLater(t, "go:rs")
	started = time.Now()
	woken, err := db.XRead(map[string]string{"go:rs": "$"}, memsharp.XReadOptions{Block: memsharp.BlockForever})
	if err != nil || len(woken["go:rs"]) != 1 || woken["go:rs"][0].Fields["n"] != "woke" {
		t.Fatalf("XRead woken = %v, %v; want the woke entry", woken, err)
	}
	if waited := time.Since(started); waited < 200*time.Millisecond {
		t.Errorf("XRead returned after %v; it should have been parked until the XADD", waited)
	}
	if pong, err := db.Ping(); err != nil || pong != "PONG" {
		t.Errorf("Ping after a blocking read = %q, %v", pong, err)
	}
}

// addLater appends {n: woke} to key from a second connection after 300ms.
func addLater(t *testing.T, key string) {
	t.Helper()

	other, err := memsharp.Dial(address())
	if err != nil {
		t.Fatalf("second connection: %v", err)
	}
	go func() {
		defer other.Close()
		time.Sleep(300 * time.Millisecond)
		_, _ = other.XAdd(key, map[string]string{"n": "woke"}, 0)
	}()
}

func TestConsumerGroups(t *testing.T) {
	db := connect(t)
	_, _ = db.Del("go:jobs", "go:trim")
	defer db.Del("go:jobs", "go:trim")

	if err := db.XGroupCreate("go:jobs", "workers", "", false); err == nil {
		t.Error("XGroupCreate on a missing key without MKSTREAM succeeded")
	}
	if err := db.XGroupCreate("go:jobs", "workers", "", true); err != nil {
		t.Fatalf("XGroupCreate MKSTREAM: %v", err)
	}
	if err := db.XGroupCreate("go:jobs", "workers", "$", false); replyCode(err) != "BUSYGROUP" {
		t.Errorf("duplicate XGroupCreate: err = %v; want BUSYGROUP", err)
	}

	if created, _ := db.XGroupCreateConsumer("go:jobs", "workers", "spare"); !created {
		t.Error("XGroupCreateConsumer reported false for a new consumer")
	}
	if created, _ := db.XGroupCreateConsumer("go:jobs", "workers", "spare"); created {
		t.Error("XGroupCreateConsumer reported true for an existing consumer")
	}

	j1, _ := db.XAdd("go:jobs", map[string]string{"task": "a"}, 0)
	j2, _ := db.XAdd("go:jobs", map[string]string{"task": "b"}, 0)
	next := map[string]string{"go:jobs": ""}

	got, err := db.XReadGroup("workers", "w1", next, memsharp.XReadGroupOptions{Count: 1})
	if err != nil || len(got["go:jobs"]) != 1 || got["go:jobs"][0].ID != j1 || got["go:jobs"][0].Fields["task"] != "a" {
		t.Fatalf("XReadGroup > = %v, %v; want j1", got, err)
	}
	if got, _ = db.XReadGroup("workers", "w1", map[string]string{"go:jobs": ">"}, memsharp.XReadGroupOptions{}); len(got["go:jobs"]) != 1 || got["go:jobs"][0].ID != j2 {
		t.Errorf("second XReadGroup = %v; want j2", got)
	}
	if got, err = db.XReadGroup("workers", "w1", next, memsharp.XReadGroupOptions{}); err != nil || got != nil {
		t.Errorf("XReadGroup with nothing new = %v, %v; want nil", got, err)
	}
	if got, err = db.XReadGroup("workers", "w1", next, memsharp.XReadGroupOptions{Block: 100 * time.Millisecond}); err != nil || got != nil {
		t.Errorf("XReadGroup block timeout = %v, %v; want nil", got, err)
	}

	history, _ := db.XReadGroup("workers", "w1", map[string]string{"go:jobs": "0"}, memsharp.XReadGroupOptions{})
	if len(history["go:jobs"]) != 2 {
		t.Errorf("XReadGroup history = %v; want both entries", history)
	}
	// History for a consumer with nothing pending is the stream with an empty list, not nil.
	if empty, _ := db.XReadGroup("workers", "w2", map[string]string{"go:jobs": "0"}, memsharp.XReadGroupOptions{}); empty == nil || len(empty["go:jobs"]) != 0 {
		t.Errorf("empty history = %#v; want {go:jobs: []}", empty)
	}
	if _, err := db.XReadGroup("nogroup", "w1", next, memsharp.XReadGroupOptions{}); replyCode(err) != "NOGROUP" {
		t.Errorf("XReadGroup on a missing group: err = %v; want NOGROUP", err)
	}

	summary, err := db.XPending("go:jobs", "workers")
	if err != nil || summary.Count != 2 || summary.Lowest != j1 || summary.Highest != j2 || summary.Consumers["w1"] != 2 || len(summary.Consumers) != 1 {
		t.Errorf("XPending = %+v, %v", summary, err)
	}

	// Two deliveries each: the history re-read above counts as one, as it does in Redis.
	pending, err := db.XPendingRange("go:jobs", "workers", memsharp.XPendingRangeOptions{})
	if err != nil || len(pending) != 2 || pending[0].ID != j1 || pending[0].Consumer != "w1" || pending[0].Deliveries != 2 {
		t.Errorf("XPendingRange = %+v, %v", pending, err)
	}
	if mine, _ := db.XPendingRange("go:jobs", "workers", memsharp.XPendingRangeOptions{Consumer: "w2"}); len(mine) != 0 {
		t.Errorf("XPendingRange for w2 = %+v; want none", mine)
	}

	if n, err := db.XAck("go:jobs", "workers", j1, "9-9"); err != nil || n != 1 {
		t.Errorf("XAck = %d, %v; want 1", n, err)
	}

	time.Sleep(60 * time.Millisecond)
	if idle, _ := db.XPendingRange("go:jobs", "workers", memsharp.XPendingRangeOptions{Idle: 50 * time.Millisecond}); len(idle) != 1 {
		t.Errorf("XPendingRange Idle = %+v; want j2", idle)
	}

	if fresh, _ := db.XClaim("go:jobs", "workers", "w2", time.Minute, []string{j2}, memsharp.XClaimOptions{}); len(fresh) != 0 {
		t.Errorf("XClaim of a fresh entry = %v; want none", fresh)
	}
	claimed, err := db.XClaim("go:jobs", "workers", "w2", 50*time.Millisecond, []string{j2}, memsharp.XClaimOptions{})
	if err != nil || len(claimed) != 1 || claimed[0].ID != j2 || claimed[0].Fields["task"] != "b" {
		t.Errorf("XClaim = %v, %v; want j2", claimed, err)
	}
	if summary, _ = db.XPending("go:jobs", "workers"); summary.Consumers["w2"] != 1 || len(summary.Consumers) != 1 {
		t.Errorf("after XClaim, consumers = %v; want w2 only", summary.Consumers)
	}
	if ids, _ := db.XClaimJustID("go:jobs", "workers", "w3", 0, []string{j2}, memsharp.XClaimOptions{}); len(ids) != 1 || ids[0] != j2 {
		t.Errorf("XClaimJustID = %v; want [j2]", ids)
	}

	_, _ = db.XClaimJustID("go:jobs", "workers", "w3", 0, []string{j2}, memsharp.XClaimOptions{Idle: 5 * time.Second, RetryCount: 7})
	if rows, _ := db.XPendingRange("go:jobs", "workers", memsharp.XPendingRangeOptions{}); len(rows) != 1 || rows[0].Deliveries != 7 || rows[0].Idle < 5*time.Second {
		t.Errorf("XClaim Idle/RetryCount = %+v", rows)
	}
	if ids, _ := db.XClaimJustID("go:jobs", "workers", "w3", 0, []string{j1}, memsharp.XClaimOptions{Force: true}); len(ids) != 1 || ids[0] != j1 {
		t.Errorf("XClaim Force = %v; want [j1]", ids)
	}

	j3, _ := db.XAdd("go:jobs", map[string]string{"task": "c"}, 0)
	_, _ = db.XReadGroup("workers", "w1", next, memsharp.XReadGroupOptions{})
	time.Sleep(60 * time.Millisecond)

	start, auto, deleted, err := db.XAutoClaim("go:jobs", "workers", "w4", 50*time.Millisecond, "", 1)
	if err != nil || start == "0-0" || len(auto) != 1 || auto[0].ID != j1 || auto[0].Fields["task"] != "a" || len(deleted) != 0 {
		t.Errorf("XAutoClaim page = %q, %v, %v, %v", start, auto, deleted, err)
	}
	start, autoIDs, _, err := db.XAutoClaimJustID("go:jobs", "workers", "w4", 0, start, 0)
	if err != nil || start != "0-0" || len(autoIDs) != 2 || autoIDs[0] != j2 || autoIDs[1] != j3 {
		t.Errorf("XAutoClaimJustID = %q, %v, %v; want 0-0 and [j2 j3]", start, autoIDs, err)
	}

	groups, err := db.XInfoGroups("go:jobs")
	if err != nil || len(groups) != 1 || groups[0].Name != "workers" || groups[0].Pending != 3 || groups[0].LastDeliveredID != j3 {
		t.Errorf("XInfoGroups = %+v, %v", groups, err)
	}
	consumers, err := db.XInfoConsumers("go:jobs", "workers")
	byName := make(map[string]memsharp.ConsumerInfo)
	for _, consumer := range consumers {
		byName[consumer.Name] = consumer
	}
	if err != nil || byName["w4"].Pending != 3 || byName["spare"].Name != "spare" || byName["spare"].Pending != 0 {
		t.Errorf("XInfoConsumers = %+v, %v", consumers, err)
	}
	stream, err := db.XInfoStream("go:jobs")
	if err != nil || stream.Length != 3 || stream.Groups != 1 || stream.LastGeneratedID != j3 ||
		stream.FirstEntry == nil || stream.FirstEntry.ID != j1 || stream.FirstEntry.Fields["task"] != "a" ||
		stream.LastEntry == nil || stream.LastEntry.ID != j3 {
		t.Errorf("XInfoStream = %+v, %v", stream, err)
	}

	if n, err := db.XGroupDelConsumer("go:jobs", "workers", "w4"); err != nil || n != 3 {
		t.Errorf("XGroupDelConsumer = %d, %v; want 3", n, err)
	}
	_ = db.XGroupSetID("go:jobs", "workers", "0")
	if replay, _ := db.XReadGroup("workers", "w5", next, memsharp.XReadGroupOptions{}); len(replay["go:jobs"]) != 3 {
		t.Errorf("after XGroupSetID 0, read = %v; want all three", replay)
	}
	_ = db.XGroupSetID("go:jobs", "workers", "")
	if after, _ := db.XReadGroup("workers", "w5", next, memsharp.XReadGroupOptions{}); after != nil {
		t.Errorf("after XGroupSetID $, read = %v; want nil", after)
	}

	_ = db.XGroupCreate("go:jobs", "fire", "0", false)
	fired, _ := db.XReadGroup("fire", "f", next, memsharp.XReadGroupOptions{NoAck: true})
	if summary, _ = db.XPending("go:jobs", "fire"); len(fired["go:jobs"]) != 3 || summary.Count != 0 || summary.Lowest != "" {
		t.Errorf("NoAck read %d entries, pending = %+v; want 3 and nothing pending", len(fired["go:jobs"]), summary)
	}

	// An entry trimmed while pending: history shows it with nil Fields, autoclaim reports it deleted.
	_ = db.XGroupCreate("go:trim", "g", "", true)
	t1, _ := db.XAdd("go:trim", map[string]string{"n": "1"}, 0)
	_, _ = db.XReadGroup("g", "c", map[string]string{"go:trim": ""}, memsharp.XReadGroupOptions{})
	for i := 0; i < 3; i++ {
		_, _ = db.XAdd("go:trim", map[string]string{"n": "x"}, 2)
	}
	trimmed, _ := db.XReadGroup("g", "c", map[string]string{"go:trim": "0"}, memsharp.XReadGroupOptions{})
	if len(trimmed["go:trim"]) != 1 || trimmed["go:trim"][0].ID != t1 || trimmed["go:trim"][0].Fields != nil {
		t.Errorf("trimmed history = %#v; want [{t1 nil}]", trimmed)
	}
	if start, claimedTrim, deletedTrim, _ := db.XAutoClaim("go:trim", "g", "c2", 0, "", 0); start != "0-0" || len(claimedTrim) != 0 || len(deletedTrim) != 1 || deletedTrim[0] != t1 {
		t.Errorf("XAutoClaim over a trimmed entry = %q, %v, %v; want 0-0, [], [t1]", start, claimedTrim, deletedTrim)
	}

	// Woken from a second connection while parked in XREADGROUP.
	addLater(t, "go:jobs")
	woken, err := db.XReadGroup("workers", "w5", next, memsharp.XReadGroupOptions{Block: 5 * time.Second})
	if err != nil || len(woken["go:jobs"]) != 1 || woken["go:jobs"][0].Fields["n"] != "woke" {
		t.Errorf("XReadGroup woken = %v, %v", woken, err)
	}

	if destroyed, _ := db.XGroupDestroy("go:jobs", "workers"); !destroyed {
		t.Error("XGroupDestroy reported false for an existing group")
	}
	if destroyed, _ := db.XGroupDestroy("go:jobs", "workers"); destroyed {
		t.Error("XGroupDestroy reported true for a missing group")
	}
}
