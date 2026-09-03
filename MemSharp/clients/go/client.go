// Package memsharp is a client for MemSharp, an in-memory database for .NET that speaks RESP.
//
// MemSharp uses the same wire protocol Redis does, so this package is a thin, dependency-free
// client over a socket.
//
//	db, err := memsharp.Dial("127.0.0.1:6380")
//	if err != nil {
//	    return err
//	}
//	defer db.Close()
//
//	if err := db.Set("symbol:BTC", "68350.25"); err != nil {
//	    return err
//	}
//	price, err := db.Get("symbol:BTC")
//
// Start a server with `memsharp serve --port 6380`.
package memsharp

import (
	"bufio"
	"errors"
	"fmt"
	"net"
	"strconv"
	"strings"
	"sync"
	"time"
)

// Client is a connection to a MemSharp server.
//
// Safe for concurrent use: a mutex serialises command round-trips, because RESP replies arrive in
// request order and two interleaved requests would each be able to read the other's reply. For
// parallel load, give each goroutine its own Client.
type Client struct {
	mu      sync.Mutex
	conn    net.Conn
	reader  *bufio.Reader
	writer  *bufio.Writer
	builder strings.Builder
}

// Options configures a Client.
type Options struct {
	// Address is the host:port to dial. Defaults to 127.0.0.1:6380.
	Address string

	// DialTimeout bounds the connect. Defaults to 10 seconds.
	DialTimeout time.Duration
}

// Dial connects to a MemSharp server at address.
func Dial(address string) (*Client, error) {
	return DialWithOptions(Options{Address: address})
}

// DialWithOptions connects with explicit options.
func DialWithOptions(options Options) (*Client, error) {
	if options.Address == "" {
		options.Address = "127.0.0.1:6380"
	}
	if options.DialTimeout == 0 {
		options.DialTimeout = 10 * time.Second
	}

	conn, err := net.DialTimeout("tcp", options.Address, options.DialTimeout)
	if err != nil {
		return nil, fmt.Errorf("memsharp: dial %s: %w", options.Address, err)
	}

	// The workload is many small request/response round-trips, which is exactly what Nagle's
	// coalescing delay ruins.
	if tcp, ok := conn.(*net.TCPConn); ok {
		_ = tcp.SetNoDelay(true)
	}

	return &Client{
		conn:   conn,
		reader: bufio.NewReaderSize(conn, 64*1024),
		writer: bufio.NewWriterSize(conn, 64*1024),
	}, nil
}

// Close closes the connection.
func (c *Client) Close() error {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.conn == nil {
		return nil
	}
	err := c.conn.Close()
	c.conn = nil
	return err
}

// Do sends one command and returns its reply. An error reply is returned as a Go error.
func (c *Client) Do(args ...any) (any, error) {
	c.mu.Lock()
	defer c.mu.Unlock()

	reply, err := c.roundTrip(args)
	if err != nil {
		return nil, err
	}
	if replyErr, ok := reply.(*Error); ok {
		return nil, replyErr
	}
	return reply, nil
}

// Pipeline sends several commands in one write and returns every reply.
//
// One round-trip for the whole batch instead of one each. Over a real network that is the single
// largest thing you can do for throughput. Error replies are returned in place as *Error values
// rather than aborting the batch, so one failing command does not hide the rest.
func (c *Client) Pipeline(commands [][]any) ([]any, error) {
	if len(commands) == 0 {
		return nil, nil
	}

	c.mu.Lock()
	defer c.mu.Unlock()

	if c.conn == nil {
		return nil, ErrClosed
	}

	c.builder.Reset()
	for _, command := range commands {
		encodeCommand(&c.builder, command)
	}
	if _, err := c.writer.WriteString(c.builder.String()); err != nil {
		return nil, err
	}
	if err := c.writer.Flush(); err != nil {
		return nil, err
	}

	replies := make([]any, len(commands))
	for i := range replies {
		reply, err := readReply(c.reader)
		if err != nil {
			return nil, err
		}
		replies[i] = reply
	}
	return replies, nil
}

func (c *Client) roundTrip(args []any) (any, error) {
	if c.conn == nil {
		return nil, ErrClosed
	}

	c.builder.Reset()
	encodeCommand(&c.builder, args)

	if _, err := c.writer.WriteString(c.builder.String()); err != nil {
		return nil, err
	}
	if err := c.writer.Flush(); err != nil {
		return nil, err
	}
	return readReply(c.reader)
}

// -- keys ------------------------------------------------------------------------------

// Ping round-trips the server.
func (c *Client) Ping() (string, error) {
	return c.text(c.Do("PING"))
}

// Set stores a string.
func (c *Client) Set(key, value string) error {
	_, err := c.Do("SET", key, value)
	return err
}

// SetEx stores a string with a lifetime.
func (c *Client) SetEx(key, value string, ttl time.Duration) error {
	_, err := c.Do("SET", key, value, "PX", ttl.Milliseconds())
	return err
}

// SetNX stores a string only if the key is absent. Reports whether it was stored.
func (c *Client) SetNX(key, value string) (bool, error) {
	reply, err := c.Do("SET", key, value, "NX")
	return reply != nil, err
}

// Get reads a string. found is false when the key is absent.
func (c *Client) Get(key string) (value string, found bool, err error) {
	reply, err := c.Do("GET", key)
	if err != nil || reply == nil {
		return "", false, err
	}
	text, _ := reply.(string)
	return text, true, nil
}

// MGet reads several strings. A missing key comes back as a nil pointer in position.
func (c *Client) MGet(keys ...string) ([]*string, error) {
	args := append([]any{"MGET"}, toAny(keys)...)
	reply, err := c.Do(args...)
	return toStringPointers(reply), err
}

// MSet writes several key/value pairs.
func (c *Client) MSet(pairs map[string]string) error {
	args := make([]any, 0, len(pairs)*2+1)
	args = append(args, "MSET")
	for key, value := range pairs {
		args = append(args, key, value)
	}
	_, err := c.Do(args...)
	return err
}

// Del removes keys and returns how many existed.
func (c *Client) Del(keys ...string) (int64, error) {
	args := append([]any{"DEL"}, toAny(keys)...)
	return c.number(c.Do(args...))
}

// Exists counts how many of the given keys exist.
func (c *Client) Exists(keys ...string) (int64, error) {
	args := append([]any{"EXISTS"}, toAny(keys)...)
	return c.number(c.Do(args...))
}

// Type reports the kind of value a key holds, or "none".
func (c *Client) Type(key string) (string, error) {
	return c.text(c.Do("TYPE", key))
}

// Incr atomically adds to an integer, treating a missing key as 0.
func (c *Client) Incr(key string, amount int64) (int64, error) {
	return c.number(c.Do("INCRBY", key, amount))
}

// IncrByFloat atomically adds to a floating-point value.
func (c *Client) IncrByFloat(key string, amount float64) (float64, error) {
	reply, err := c.Do("INCRBYFLOAT", key, amount)
	if err != nil {
		return 0, err
	}
	text, _ := reply.(string)
	return strconv.ParseFloat(text, 64)
}

// Expire sets a lifetime. Reports false if the key is absent.
func (c *Client) Expire(key string, ttl time.Duration) (bool, error) {
	value, err := c.number(c.Do("PEXPIRE", key, ttl.Milliseconds()))
	return value == 1, err
}

// TTL reports the remaining lifetime. hasTTL is false when the key is permanent or absent.
func (c *Client) TTL(key string) (ttl time.Duration, hasTTL bool, err error) {
	milliseconds, err := c.number(c.Do("PTTL", key))
	if err != nil || milliseconds < 0 {
		return 0, false, err
	}
	return time.Duration(milliseconds) * time.Millisecond, true, nil
}

// Persist clears a key's expiry.
func (c *Client) Persist(key string) (bool, error) {
	value, err := c.number(c.Do("PERSIST", key))
	return value == 1, err
}

// Keys returns every key matching a glob. Prefer Scan on a large keyspace.
func (c *Client) Keys(pattern string) ([]string, error) {
	reply, err := c.Do("KEYS", pattern)
	return toStrings(reply), err
}

// Scan iterates the keyspace a page at a time, calling fn for each key.
//
// The loop runs until the server returns a zero cursor, which is the same contract Redis uses.
// Returning an error from fn stops the walk and returns that error.
func (c *Client) Scan(pattern string, count int, fn func(key string) error) error {
	cursor := "0"
	for {
		reply, err := c.Do("SCAN", cursor, "MATCH", pattern, "COUNT", count)
		if err != nil {
			return err
		}

		parts, ok := reply.([]any)
		if !ok || len(parts) != 2 {
			return errors.New("memsharp: malformed SCAN reply")
		}

		next, _ := parts[0].(string)
		for _, key := range toStrings(parts[1]) {
			if err := fn(key); err != nil {
				return err
			}
		}

		cursor = next
		if cursor == "0" {
			return nil
		}
	}
}

// -- lists -----------------------------------------------------------------------------

// LPush pushes onto the head of a list and returns the new length.
func (c *Client) LPush(key string, values ...string) (int64, error) {
	args := append([]any{"LPUSH", key}, toAny(values)...)
	return c.number(c.Do(args...))
}

// RPush pushes onto the tail of a list and returns the new length.
func (c *Client) RPush(key string, values ...string) (int64, error) {
	args := append([]any{"RPUSH", key}, toAny(values)...)
	return c.number(c.Do(args...))
}

// LPop removes and returns the head.
func (c *Client) LPop(key string) (string, bool, error) {
	reply, err := c.Do("LPOP", key)
	if err != nil || reply == nil {
		return "", false, err
	}
	text, _ := reply.(string)
	return text, true, nil
}

// RPop removes and returns the tail.
func (c *Client) RPop(key string) (string, bool, error) {
	reply, err := c.Do("RPOP", key)
	if err != nil || reply == nil {
		return "", false, err
	}
	text, _ := reply.(string)
	return text, true, nil
}

// LRange returns elements in an inclusive index range. Negative indices count back from the end.
func (c *Client) LRange(key string, start, stop int) ([]string, error) {
	reply, err := c.Do("LRANGE", key, start, stop)
	return toStrings(reply), err
}

// LLen returns the number of elements in a list.
func (c *Client) LLen(key string) (int64, error) {
	return c.number(c.Do("LLEN", key))
}

// LTrim keeps only an index range - the way to cap a feed at a fixed length.
func (c *Client) LTrim(key string, start, stop int) error {
	_, err := c.Do("LTRIM", key, start, stop)
	return err
}

// -- hashes ----------------------------------------------------------------------------

// HSet sets fields and returns how many were new.
func (c *Client) HSet(key string, fields map[string]string) (int64, error) {
	args := make([]any, 0, len(fields)*2+2)
	args = append(args, "HSET", key)
	for field, value := range fields {
		args = append(args, field, value)
	}
	return c.number(c.Do(args...))
}

// HGet reads one field.
func (c *Client) HGet(key, field string) (string, bool, error) {
	reply, err := c.Do("HGET", key, field)
	if err != nil || reply == nil {
		return "", false, err
	}
	text, _ := reply.(string)
	return text, true, nil
}

// HGetAll returns every field and value.
func (c *Client) HGetAll(key string) (map[string]string, error) {
	reply, err := c.Do("HGETALL", key)
	return toMap(reply), err
}

// HDel removes fields and returns how many existed.
func (c *Client) HDel(key string, fields ...string) (int64, error) {
	args := append([]any{"HDEL", key}, toAny(fields)...)
	return c.number(c.Do(args...))
}

// HIncrBy atomically adds to an integer field.
func (c *Client) HIncrBy(key, field string, amount int64) (int64, error) {
	return c.number(c.Do("HINCRBY", key, field, amount))
}

// -- sets ------------------------------------------------------------------------------

// SAdd adds members and returns how many were new.
func (c *Client) SAdd(key string, members ...string) (int64, error) {
	args := append([]any{"SADD", key}, toAny(members)...)
	return c.number(c.Do(args...))
}

// SRem removes members and returns how many were present.
func (c *Client) SRem(key string, members ...string) (int64, error) {
	args := append([]any{"SREM", key}, toAny(members)...)
	return c.number(c.Do(args...))
}

// SMembers returns every member of a set.
func (c *Client) SMembers(key string) ([]string, error) {
	reply, err := c.Do("SMEMBERS", key)
	return toStrings(reply), err
}

// SIsMember reports whether a set contains a member.
func (c *Client) SIsMember(key, member string) (bool, error) {
	value, err := c.number(c.Do("SISMEMBER", key, member))
	return value == 1, err
}

// -- sorted sets -----------------------------------------------------------------------

// ScoredMember is a sorted set member with its score.
type ScoredMember struct {
	Member string
	Score  float64
}

// ZAdd adds scored members and returns how many were new.
func (c *Client) ZAdd(key string, members ...ScoredMember) (int64, error) {
	args := make([]any, 0, len(members)*2+2)
	args = append(args, "ZADD", key)
	for _, member := range members {
		args = append(args, member.Score, member.Member)
	}
	return c.number(c.Do(args...))
}

// ZRange returns members in an inclusive rank range.
func (c *Client) ZRange(key string, start, stop int, descending bool) ([]string, error) {
	command := "ZRANGE"
	if descending {
		command = "ZREVRANGE"
	}
	reply, err := c.Do(command, key, start, stop)
	return toStrings(reply), err
}

// ZRangeWithScores returns members in a rank range, with their scores.
func (c *Client) ZRangeWithScores(key string, start, stop int, descending bool) ([]ScoredMember, error) {
	command := "ZRANGE"
	if descending {
		command = "ZREVRANGE"
	}
	reply, err := c.Do(command, key, start, stop, "WITHSCORES")
	if err != nil {
		return nil, err
	}
	return pairScores(reply), nil
}

// ZRangeByScore returns members whose score falls in an inclusive range.
func (c *Client) ZRangeByScore(key string, low, high float64) ([]string, error) {
	reply, err := c.Do("ZRANGEBYSCORE", key, low, high)
	return toStrings(reply), err
}

// ZScore returns a member's score. found is false when the member is absent.
func (c *Client) ZScore(key, member string) (score float64, found bool, err error) {
	reply, err := c.Do("ZSCORE", key, member)
	if err != nil || reply == nil {
		return 0, false, err
	}

	text, _ := reply.(string)
	value, err := strconv.ParseFloat(text, 64)
	return value, err == nil, err
}

// ZCard returns the number of members.
func (c *Client) ZCard(key string) (int64, error) {
	return c.number(c.Do("ZCARD", key))
}

// -- streams ---------------------------------------------------------------------------

// StreamEntry is one entry of a stream.
type StreamEntry struct {
	ID     string
	Fields map[string]string
}

// XAdd appends an entry and returns the assigned ms-seq id.
//
// maxLen greater than 0 caps the stream, dropping the oldest entries.
func (c *Client) XAdd(key string, fields map[string]string, maxLen int) (string, error) {
	args := make([]any, 0, len(fields)*2+5)
	args = append(args, "XADD", key)
	if maxLen > 0 {
		args = append(args, "MAXLEN", maxLen)
	}
	args = append(args, "*")
	for field, value := range fields {
		args = append(args, field, value)
	}
	return c.text(c.Do(args...))
}

// XRange returns entries in an id range, oldest first. Use "-" and "+" for the ends of the stream.
func (c *Client) XRange(key, start, end string, count int) ([]StreamEntry, error) {
	args := []any{"XRANGE", key, start, end}
	if count > 0 {
		args = append(args, "COUNT", count)
	}

	reply, err := c.Do(args...)
	if err != nil {
		return nil, err
	}

	rows, _ := reply.([]any)
	entries := make([]StreamEntry, 0, len(rows))
	for _, row := range rows {
		parts, ok := row.([]any)
		if !ok || len(parts) != 2 {
			continue
		}
		id, _ := parts[0].(string)
		entries = append(entries, StreamEntry{ID: id, Fields: toMap(parts[1])})
	}
	return entries, nil
}

// XLen returns the number of entries.
func (c *Client) XLen(key string) (int64, error) {
	return c.number(c.Do("XLEN", key))
}

// -- time series -----------------------------------------------------------------------

// Sample is one time-series observation.
type Sample struct {
	Timestamp int64
	Value     float64
}

// TSCreate creates a time series, optionally capped at retention samples.
func (c *Client) TSCreate(key string, retention int) error {
	_, err := c.Do("TS.CREATE", key, "RETENTION", retention)
	return err
}

// TSAdd appends a sample at an explicit timestamp and returns the timestamp written.
//
// The timestamp must be at least the series' latest one: a series is append-only, and out-of-order
// writes are rejected rather than sorted, which is what keeps range queries a binary search.
//
// Use TSAddNow to have the server stamp the sample instead. There is deliberately no sentinel
// value for "now" here - 0 is a legitimate Unix timestamp, and a client that stole it would make
// the epoch unwritable.
func (c *Client) TSAdd(key string, value float64, timestamp int64) (int64, error) {
	return c.number(c.Do("TS.ADD", key, timestamp, value))
}

// TSAddNow appends a sample stamped with the server's clock and returns that timestamp.
func (c *Client) TSAddNow(key string, value float64) (int64, error) {
	return c.number(c.Do("TS.ADD", key, "*", value))
}

// TSRange returns samples in an inclusive timestamp range.
func (c *Client) TSRange(key string, start, end int64) ([]Sample, error) {
	reply, err := c.Do("TS.RANGE", key, start, end)
	return toSamples(reply), err
}

// TSAggregate folds a range into fixed-width buckets.
//
// how is one of avg, min, max, sum, count, first, last.
func (c *Client) TSAggregate(key string, start, end, bucketMs int64, how string) ([]Sample, error) {
	reply, err := c.Do("TS.AGGREGATE", key, start, end, bucketMs, how)
	return toSamples(reply), err
}

// -- pub/sub ---------------------------------------------------------------------------

// Message is one delivered pub/sub message.
type Message struct {
	Channel string
	Payload string

	// Pattern is set when the message arrived through a pattern subscription.
	Pattern string
}

// Publish sends a message and returns how many subscribers received it.
func (c *Client) Publish(channel, message string) (int64, error) {
	return c.number(c.Do("PUBLISH", channel, message))
}

// Subscribe subscribes to channels and calls fn for each message, blocking until the connection
// closes or fn returns an error.
//
// This takes over the connection: the server may push a message between any request and its reply,
// so a subscribed client cannot also run ordinary commands. Use a second client.
func (c *Client) Subscribe(fn func(Message) error, channels ...string) error {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.conn == nil {
		return ErrClosed
	}

	args := append([]any{"SUBSCRIBE"}, toAny(channels)...)
	c.builder.Reset()
	encodeCommand(&c.builder, args)

	if _, err := c.writer.WriteString(c.builder.String()); err != nil {
		return err
	}
	if err := c.writer.Flush(); err != nil {
		return err
	}

	for {
		reply, err := readReply(c.reader)
		if err != nil {
			if errors.Is(err, ErrClosed) {
				return nil
			}
			return err
		}

		// Every push is an array whose head names the kind. The subscribe acknowledgement has the
		// same shape, so it is filtered out here rather than surfacing as a message.
		parts, ok := reply.([]any)
		if !ok || len(parts) < 3 {
			continue
		}

		kind, _ := parts[0].(string)
		switch kind {
		case "message":
			channel, _ := parts[1].(string)
			payload, _ := parts[2].(string)
			if err := fn(Message{Channel: channel, Payload: payload}); err != nil {
				return err
			}
		case "pmessage":
			if len(parts) < 4 {
				continue
			}
			pattern, _ := parts[1].(string)
			channel, _ := parts[2].(string)
			payload, _ := parts[3].(string)
			if err := fn(Message{Channel: channel, Payload: payload, Pattern: pattern}); err != nil {
				return err
			}
		}
	}
}

// -- query and server ------------------------------------------------------------------

// SQL runs a SELECT against the keyspace and returns rows keyed by column name.
//
// The server sends the column names as the first row; this pairs them with each row so callers work
// with names rather than positions.
func (c *Client) SQL(query string) ([]map[string]string, error) {
	reply, err := c.Do("SQL", query)
	if err != nil {
		return nil, err
	}

	rows, ok := reply.([]any)
	if !ok || len(rows) == 0 {
		return nil, nil
	}

	columns := toStrings(rows[0])
	result := make([]map[string]string, 0, len(rows)-1)
	for _, row := range rows[1:] {
		values := toStrings(row)
		record := make(map[string]string, len(columns))
		for i, column := range columns {
			if i < len(values) {
				record[column] = values[i]
			}
		}
		result = append(result, record)
	}
	return result, nil
}

// SQLDelete runs a DELETE against the keyspace and returns the number of rows removed.
func (c *Client) SQLDelete(query string) (int64, error) {
	return c.number(c.Do("SQL", query))
}

// Info returns server statistics, parsed into a map. Section headings are dropped.
func (c *Client) Info() (map[string]string, error) {
	text, err := c.text(c.Do("INFO"))
	if err != nil {
		return nil, err
	}

	result := make(map[string]string)
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		if name, value, found := strings.Cut(line, ":"); found {
			result[name] = value
		}
	}
	return result, nil
}

// DBSize returns the number of keys in the database.
func (c *Client) DBSize() (int64, error) {
	return c.number(c.Do("DBSIZE"))
}

// FlushDB removes every key.
func (c *Client) FlushDB() error {
	_, err := c.Do("FLUSHDB")
	return err
}

// Save writes a snapshot synchronously.
func (c *Client) Save() error {
	_, err := c.Do("SAVE")
	return err
}

// -- helpers ---------------------------------------------------------------------------

func (c *Client) text(reply any, err error) (string, error) {
	if err != nil {
		return "", err
	}
	value, _ := reply.(string)
	return value, nil
}

func (c *Client) number(reply any, err error) (int64, error) {
	if err != nil {
		return 0, err
	}

	switch value := reply.(type) {
	case int64:
		return value, nil
	case string:
		return strconv.ParseInt(value, 10, 64)
	default:
		return 0, nil
	}
}

func toAny(values []string) []any {
	result := make([]any, len(values))
	for i, value := range values {
		result[i] = value
	}
	return result
}

func pairScores(reply any) []ScoredMember {
	flat := toStrings(reply)
	result := make([]ScoredMember, 0, len(flat)/2)
	for i := 0; i+1 < len(flat); i += 2 {
		score, _ := strconv.ParseFloat(flat[i+1], 64)
		result = append(result, ScoredMember{Member: flat[i], Score: score})
	}
	return result
}

func toSamples(reply any) []Sample {
	rows, _ := reply.([]any)
	samples := make([]Sample, 0, len(rows))
	for _, row := range rows {
		parts, ok := row.([]any)
		if !ok || len(parts) != 2 {
			continue
		}

		var timestamp int64
		switch value := parts[0].(type) {
		case int64:
			timestamp = value
		case string:
			timestamp, _ = strconv.ParseInt(value, 10, 64)
		}

		text, _ := parts[1].(string)
		value, _ := strconv.ParseFloat(text, 64)
		samples = append(samples, Sample{Timestamp: timestamp, Value: value})
	}
	return samples
}
