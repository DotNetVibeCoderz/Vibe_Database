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

// StrLen returns the length of a string in UTF-8 bytes, not characters: "héllo" is 6.
func (c *Client) StrLen(key string) (int64, error) {
	return c.number(c.Do("STRLEN", key))
}

// GetRange returns a substring by inclusive byte offsets. Negative offsets count back from the end.
//
// The server refuses a range that would split a multi-byte character rather than hand back invalid
// UTF-8, so that case is an *Error.
func (c *Client) GetRange(key string, start, end int) (string, error) {
	return c.text(c.Do("GETRANGE", key, start, end))
}

// SetRange overwrites from a byte offset, zero-padding a short string, and returns the new length in
// bytes.
func (c *Client) SetRange(key string, offset int, value string) (int64, error) {
	return c.number(c.Do("SETRANGE", key, offset, value))
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
// Returning an error from fn stops the walk and returns that error. The cursor is opaque, can
// exceed 2^32, and is passed back exactly as the server sent it; a page can be empty while it is
// not zero.
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

// LPosOptions narrows an LPos search. Zero values leave an option unset.
type LPosOptions struct {
	// Rank picks which match to start from; negative searches from the tail.
	Rank int

	// MaxLen compares at most this many elements.
	MaxLen int
}

// LPos returns the index of a matching element. found is false when nothing matches.
func (c *Client) LPos(key, element string, options LPosOptions) (index int64, found bool, err error) {
	reply, err := c.Do(lposArgs(key, element, options)...)
	if err != nil || reply == nil {
		return 0, false, err
	}
	index, err = c.number(reply, nil)
	return index, err == nil, err
}

// LPosCount returns the indices of up to count matches; count 0 means every match.
func (c *Client) LPosCount(key, element string, count int, options LPosOptions) ([]int64, error) {
	args := append(lposArgs(key, element, options), "COUNT", count)
	reply, err := c.Do(args...)
	if err != nil {
		return nil, err
	}
	return toInts(reply), nil
}

func lposArgs(key, element string, options LPosOptions) []any {
	args := []any{"LPOS", key, element}
	if options.Rank != 0 {
		args = append(args, "RANK", options.Rank)
	}
	if options.MaxLen > 0 {
		args = append(args, "MAXLEN", options.MaxLen)
	}
	return args
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

// SInterStore stores the intersection of sets and returns its size. An empty result deletes
// destination.
func (c *Client) SInterStore(destination string, keys ...string) (int64, error) {
	args := append([]any{"SINTERSTORE", destination}, toAny(keys)...)
	return c.number(c.Do(args...))
}

// SUnionStore stores the union of sets and returns its size.
func (c *Client) SUnionStore(destination string, keys ...string) (int64, error) {
	args := append([]any{"SUNIONSTORE", destination}, toAny(keys)...)
	return c.number(c.Do(args...))
}

// SDiffStore stores the members of the first set found in none of the others and returns its size.
func (c *Client) SDiffStore(destination string, keys ...string) (int64, error) {
	args := append([]any{"SDIFFSTORE", destination}, toAny(keys)...)
	return c.number(c.Do(args...))
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

// ZStoreOptions configures ZUnionStore and ZInterStore. Zero values leave an option unset.
type ZStoreOptions struct {
	// Weights multiply each source's scores, in key order.
	Weights []float64

	// Aggregate is "sum" (the default), "min" or "max".
	Aggregate string
}

// ZUnionStore stores the union of sorted sets and returns its size. A plain set among keys scores
// every member 1.
func (c *Client) ZUnionStore(destination string, keys []string, options ZStoreOptions) (int64, error) {
	return c.number(c.Do(zstoreArgs("ZUNIONSTORE", destination, keys, options)...))
}

// ZInterStore stores the intersection of sorted sets and returns its size.
func (c *Client) ZInterStore(destination string, keys []string, options ZStoreOptions) (int64, error) {
	return c.number(c.Do(zstoreArgs("ZINTERSTORE", destination, keys, options)...))
}

func zstoreArgs(command, destination string, keys []string, options ZStoreOptions) []any {
	args := append([]any{command, destination, len(keys)}, toAny(keys)...)
	if len(options.Weights) > 0 {
		args = append(args, "WEIGHTS")
		for _, weight := range options.Weights {
			args = append(args, weight)
		}
	}
	if options.Aggregate != "" {
		args = append(args, "AGGREGATE", strings.ToUpper(options.Aggregate))
	}
	return args
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

	return toEntries(reply), nil
}

// XLen returns the number of entries.
func (c *Client) XLen(key string) (int64, error) {
	return c.number(c.Do("XLEN", key))
}

// BlockForever is the Block value that waits with no timeout, which BLOCK 0 means on the wire. A
// zero Block means "do not block at all", so the wire's 0 needs a name of its own.
const BlockForever time.Duration = -1

// XReadOptions configures XRead. Zero values leave an option unset.
type XReadOptions struct {
	Count int

	// Block waits up to this long for an entry; BlockForever waits with no timeout. A blocked read
	// holds the client's mutex, so other goroutines sharing the Client wait behind it.
	Block time.Duration
}

// XRead returns entries after the given ids, keyed by stream. streams maps each key to the id to
// read after; "" means "$", only what arrives from now on.
//
// The result is nil when nothing is new (or the block timed out). Only streams that returned
// something appear in it.
func (c *Client) XRead(streams map[string]string, options XReadOptions) (map[string][]StreamEntry, error) {
	args := []any{"XREAD"}
	if options.Count > 0 {
		args = append(args, "COUNT", options.Count)
	}
	args = appendBlock(args, options.Block)
	args = appendStreams(args, streams, "$")
	return toStreamReply(c.Do(args...))
}

// XGroupCreate creates a consumer group that delivers entries after id ("" or "$": only new ones).
//
// mkStream creates an empty stream when the key is absent; without it that is an error. A group that
// already exists is an *Error with Code "BUSYGROUP".
func (c *Client) XGroupCreate(key, group, id string, mkStream bool) error {
	if id == "" {
		id = "$"
	}
	args := []any{"XGROUP", "CREATE", key, group, id}
	if mkStream {
		args = append(args, "MKSTREAM")
	}
	_, err := c.Do(args...)
	return err
}

// XGroupDestroy removes a consumer group and its pending entries. Reports false if it did not exist.
func (c *Client) XGroupDestroy(key, group string) (bool, error) {
	value, err := c.number(c.Do("XGROUP", "DESTROY", key, group))
	return value == 1, err
}

// XGroupCreateConsumer registers a consumer ahead of its first read. Reports false if it already
// existed.
func (c *Client) XGroupCreateConsumer(key, group, consumer string) (bool, error) {
	value, err := c.number(c.Do("XGROUP", "CREATECONSUMER", key, group, consumer))
	return value == 1, err
}

// XGroupDelConsumer removes a consumer and returns how many pending entries it still held - those
// are dropped.
func (c *Client) XGroupDelConsumer(key, group, consumer string) (int64, error) {
	return c.number(c.Do("XGROUP", "DELCONSUMER", key, group, consumer))
}

// XGroupSetID moves a group's last-delivered id, to replay history ("0") or skip it ("" or "$").
func (c *Client) XGroupSetID(key, group, id string) error {
	if id == "" {
		id = "$"
	}
	_, err := c.Do("XGROUP", "SETID", key, group, id)
	return err
}

// XReadGroupOptions configures XReadGroup. Zero values leave an option unset.
type XReadGroupOptions struct {
	Count int

	// Block works as in XReadOptions.
	Block time.Duration

	// NoAck delivers without adding to the pending list.
	NoAck bool
}

// XReadGroup reads as consumer in group, in the same shape as XRead.
//
// An id of "" or ">" means entries never delivered to the group, and the result is nil if there are
// none. An explicit id instead re-reads this consumer's own pending history, where an entry trimmed
// from the stream since comes back with nil Fields.
func (c *Client) XReadGroup(group, consumer string, streams map[string]string, options XReadGroupOptions) (map[string][]StreamEntry, error) {
	args := []any{"XREADGROUP", "GROUP", group, consumer}
	if options.Count > 0 {
		args = append(args, "COUNT", options.Count)
	}
	args = appendBlock(args, options.Block)
	if options.NoAck {
		args = append(args, "NOACK")
	}
	args = appendStreams(args, streams, ">")
	return toStreamReply(c.Do(args...))
}

// XAck acknowledges entries, removing them from the pending list, and returns how many were pending.
func (c *Client) XAck(key, group string, ids ...string) (int64, error) {
	args := append([]any{"XACK", key, group}, toAny(ids)...)
	return c.number(c.Do(args...))
}

// PendingSummary is the summary form of XPENDING.
type PendingSummary struct {
	Count int64

	// Lowest and Highest are "" when nothing is pending.
	Lowest  string
	Highest string

	// Consumers is the pending count per consumer.
	Consumers map[string]int64
}

// XPending returns a group's pending summary.
func (c *Client) XPending(key, group string) (PendingSummary, error) {
	reply, err := c.Do("XPENDING", key, group)
	if err != nil {
		return PendingSummary{}, err
	}

	parts, ok := reply.([]any)
	if !ok || len(parts) != 4 {
		return PendingSummary{}, errors.New("memsharp: malformed XPENDING reply")
	}

	summary := PendingSummary{Consumers: make(map[string]int64)}
	summary.Count, _ = c.number(parts[0], nil)
	summary.Lowest, _ = parts[1].(string)
	summary.Highest, _ = parts[2].(string)

	// The server sends each consumer's count as a bulk string, as Redis does; number parses both.
	rows, _ := parts[3].([]any)
	for _, row := range rows {
		pair, ok := row.([]any)
		if !ok || len(pair) != 2 {
			continue
		}
		name, _ := pair[0].(string)
		summary.Consumers[name], _ = c.number(pair[1], nil)
	}
	return summary, nil
}

// XPendingRangeOptions configures XPendingRange. Zero values leave an option unset.
type XPendingRangeOptions struct {
	// Start and End bound the id range; they default to "-" and "+".
	Start string
	End   string

	// Count defaults to 100.
	Count int

	// Consumer limits the rows to one consumer's entries.
	Consumer string

	// Idle limits the rows to entries idle at least this long.
	Idle time.Duration
}

// PendingEntry is one row of the range form of XPENDING.
type PendingEntry struct {
	ID       string
	Consumer string

	// Idle is the time since the entry was last delivered.
	Idle time.Duration

	// Deliveries is how many times it has been delivered.
	Deliveries int64
}

// XPendingRange returns pending entries in an id range.
func (c *Client) XPendingRange(key, group string, options XPendingRangeOptions) ([]PendingEntry, error) {
	if options.Start == "" {
		options.Start = "-"
	}
	if options.End == "" {
		options.End = "+"
	}
	if options.Count <= 0 {
		options.Count = 100
	}

	args := []any{"XPENDING", key, group}
	if options.Idle > 0 {
		args = append(args, "IDLE", options.Idle.Milliseconds())
	}
	args = append(args, options.Start, options.End, options.Count)
	if options.Consumer != "" {
		args = append(args, options.Consumer)
	}

	reply, err := c.Do(args...)
	if err != nil {
		return nil, err
	}

	rows, _ := reply.([]any)
	result := make([]PendingEntry, 0, len(rows))
	for _, row := range rows {
		parts, ok := row.([]any)
		if !ok || len(parts) != 4 {
			continue
		}
		var entry PendingEntry
		entry.ID, _ = parts[0].(string)
		entry.Consumer, _ = parts[1].(string)
		idle, _ := c.number(parts[2], nil)
		entry.Idle = time.Duration(idle) * time.Millisecond
		entry.Deliveries, _ = c.number(parts[3], nil)
		result = append(result, entry)
	}
	return result, nil
}

// XClaimOptions configures XClaim and XClaimJustID. Zero values leave an option unset - so a
// RetryCount of 0 cannot be requested, which is also the count a fresh entry starts from.
type XClaimOptions struct {
	// Idle sets the claimed entries' idle time.
	Idle time.Duration

	// Time sets their last-delivery time.
	Time time.Time

	// RetryCount sets their delivery count.
	RetryCount int

	// Force claims ids that are not pending at all.
	Force bool

	LastID string
}

// XClaim takes over pending entries that have been idle at least minIdle, and returns them.
func (c *Client) XClaim(key, group, consumer string, minIdle time.Duration, ids []string, options XClaimOptions) ([]StreamEntry, error) {
	reply, err := c.Do(xclaimArgs(key, group, consumer, minIdle, ids, options, false)...)
	if err != nil {
		return nil, err
	}
	return toEntries(reply), nil
}

// XClaimJustID is XClaim returning only the claimed ids, and leaving their delivery count alone.
func (c *Client) XClaimJustID(key, group, consumer string, minIdle time.Duration, ids []string, options XClaimOptions) ([]string, error) {
	reply, err := c.Do(xclaimArgs(key, group, consumer, minIdle, ids, options, true)...)
	return toStrings(reply), err
}

func xclaimArgs(key, group, consumer string, minIdle time.Duration, ids []string, options XClaimOptions, justID bool) []any {
	args := append([]any{"XCLAIM", key, group, consumer, minIdle.Milliseconds()}, toAny(ids)...)
	if options.Idle > 0 {
		args = append(args, "IDLE", options.Idle.Milliseconds())
	}
	if !options.Time.IsZero() {
		args = append(args, "TIME", options.Time.UnixMilli())
	}
	if options.RetryCount > 0 {
		args = append(args, "RETRYCOUNT", options.RetryCount)
	}
	if options.Force {
		args = append(args, "FORCE")
	}
	if justID {
		args = append(args, "JUSTID")
	}
	if options.LastID != "" {
		args = append(args, "LASTID", options.LastID)
	}
	return args
}

// XAutoClaim scans the pending list from start ("" means "0-0") and claims up to count entries
// (0: the server's default) idle at least minIdle.
//
// next is "0-0" once the sweep is complete. deleted lists ids that were pending but trimmed from the
// stream; they are dropped from the pending list.
func (c *Client) XAutoClaim(key, group, consumer string, minIdle time.Duration, start string, count int) (next string, claimed []StreamEntry, deleted []string, err error) {
	parts, err := c.xautoclaim(key, group, consumer, minIdle, start, count, false)
	if err != nil {
		return "", nil, nil, err
	}
	next, _ = parts[0].(string)
	return next, toEntries(parts[1]), toStrings(parts[2]), nil
}

// XAutoClaimJustID is XAutoClaim returning only the claimed ids.
func (c *Client) XAutoClaimJustID(key, group, consumer string, minIdle time.Duration, start string, count int) (next string, claimed []string, deleted []string, err error) {
	parts, err := c.xautoclaim(key, group, consumer, minIdle, start, count, true)
	if err != nil {
		return "", nil, nil, err
	}
	next, _ = parts[0].(string)
	return next, toStrings(parts[1]), toStrings(parts[2]), nil
}

func (c *Client) xautoclaim(key, group, consumer string, minIdle time.Duration, start string, count int, justID bool) ([]any, error) {
	if start == "" {
		start = "0-0"
	}
	args := []any{"XAUTOCLAIM", key, group, consumer, minIdle.Milliseconds(), start}
	if count > 0 {
		args = append(args, "COUNT", count)
	}
	if justID {
		args = append(args, "JUSTID")
	}

	reply, err := c.Do(args...)
	if err != nil {
		return nil, err
	}
	parts, ok := reply.([]any)
	if !ok || len(parts) != 3 {
		return nil, errors.New("memsharp: malformed XAUTOCLAIM reply")
	}
	return parts, nil
}

// GroupInfo is one row of XINFO GROUPS.
type GroupInfo struct {
	Name            string
	Consumers       int64
	Pending         int64
	LastDeliveredID string
}

// XInfoGroups describes every consumer group on a stream.
func (c *Client) XInfoGroups(key string) ([]GroupInfo, error) {
	reply, err := c.Do("XINFO", "GROUPS", key)
	if err != nil {
		return nil, err
	}

	rows, _ := reply.([]any)
	result := make([]GroupInfo, 0, len(rows))
	for _, row := range rows {
		fields := toAnyMap(row)
		var info GroupInfo
		info.Name, _ = fields["name"].(string)
		info.Consumers, _ = c.number(fields["consumers"], nil)
		info.Pending, _ = c.number(fields["pending"], nil)
		info.LastDeliveredID, _ = fields["last-delivered-id"].(string)
		result = append(result, info)
	}
	return result, nil
}

// ConsumerInfo is one row of XINFO CONSUMERS.
type ConsumerInfo struct {
	Name    string
	Pending int64

	// Idle is the time since the consumer last read.
	Idle time.Duration
}

// XInfoConsumers describes every consumer in a group.
func (c *Client) XInfoConsumers(key, group string) ([]ConsumerInfo, error) {
	reply, err := c.Do("XINFO", "CONSUMERS", key, group)
	if err != nil {
		return nil, err
	}

	rows, _ := reply.([]any)
	result := make([]ConsumerInfo, 0, len(rows))
	for _, row := range rows {
		fields := toAnyMap(row)
		var info ConsumerInfo
		info.Name, _ = fields["name"].(string)
		info.Pending, _ = c.number(fields["pending"], nil)
		idle, _ := c.number(fields["idle"], nil)
		info.Idle = time.Duration(idle) * time.Millisecond
		result = append(result, info)
	}
	return result, nil
}

// StreamInfo is the reply to XINFO STREAM.
type StreamInfo struct {
	Length          int64
	LastGeneratedID string
	Groups          int64

	// FirstEntry and LastEntry are nil when the stream is empty.
	FirstEntry *StreamEntry
	LastEntry  *StreamEntry
}

// XInfoStream summarises a stream.
func (c *Client) XInfoStream(key string) (StreamInfo, error) {
	reply, err := c.Do("XINFO", "STREAM", key)
	if err != nil {
		return StreamInfo{}, err
	}

	fields := toAnyMap(reply)
	var info StreamInfo
	info.Length, _ = c.number(fields["length"], nil)
	info.LastGeneratedID, _ = fields["last-generated-id"].(string)
	info.Groups, _ = c.number(fields["groups"], nil)
	if entry, ok := toEntry(fields["first-entry"]); ok {
		info.FirstEntry = &entry
	}
	if entry, ok := toEntry(fields["last-entry"]); ok {
		info.LastEntry = &entry
	}
	return info, nil
}

func appendBlock(args []any, block time.Duration) []any {
	switch {
	case block == BlockForever:
		return append(args, "BLOCK", 0)
	case block > 0:
		// Rounded up: a sub-millisecond block would otherwise become BLOCK 0, which waits forever.
		return append(args, "BLOCK", (block + time.Millisecond - 1).Milliseconds())
	default:
		return args
	}
}

// appendStreams writes STREAMS key... id..., every key first and then every id in the same order.
func appendStreams(args []any, streams map[string]string, defaultID string) []any {
	keys := make([]string, 0, len(streams))
	for key := range streams {
		keys = append(keys, key)
	}

	args = append(args, "STREAMS")
	for _, key := range keys {
		args = append(args, key)
	}
	for _, key := range keys {
		id := streams[key]
		if id == "" {
			id = defaultID
		}
		args = append(args, id)
	}
	return args
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
//
// With no arguments every section is returned; name sections ("server", "memory", "keyspace", ...)
// to fetch only those.
func (c *Client) Info(sections ...string) (map[string]string, error) {
	args := append([]any{"INFO"}, toAny(sections)...)
	text, err := c.text(c.Do(args...))
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

// ConfigGet returns configuration values whose names match the globs (default "*"). Read-only:
// MemSharp is configured when it is constructed, so there is no ConfigSet.
func (c *Client) ConfigGet(patterns ...string) (map[string]string, error) {
	if len(patterns) == 0 {
		patterns = []string{"*"}
	}
	args := append([]any{"CONFIG", "GET"}, toAny(patterns)...)
	reply, err := c.Do(args...)
	return toMap(reply), err
}

// ConfigResetStat zeroes the server's statistics counters.
func (c *Client) ConfigResetStat() error {
	_, err := c.Do("CONFIG", "RESETSTAT")
	return err
}

// MemoryUsage returns the approximate bytes a key and its value occupy. found is false when the key
// is absent. samples bounds how many elements of a collection are measured; 0 is the server default.
func (c *Client) MemoryUsage(key string, samples int) (bytes int64, found bool, err error) {
	args := []any{"MEMORY", "USAGE", key}
	if samples > 0 {
		args = append(args, "SAMPLES", samples)
	}
	reply, err := c.Do(args...)
	if err != nil || reply == nil {
		return 0, false, err
	}
	bytes, err = c.number(reply, nil)
	return bytes, err == nil, err
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

func toInts(reply any) []int64 {
	items, _ := reply.([]any)
	result := make([]int64, 0, len(items))
	for _, item := range items {
		if value, ok := item.(int64); ok {
			result = append(result, value)
		}
	}
	return result
}

// toAnyMap pairs a flat [k, v, k, v] reply into a map, keeping each value's reply type - XINFO mixes
// integers, strings and nested entries.
func toAnyMap(reply any) map[string]any {
	items, _ := reply.([]any)
	result := make(map[string]any, len(items)/2)
	for i := 0; i+1 < len(items); i += 2 {
		if name, ok := items[i].(string); ok {
			result[name] = items[i+1]
		}
	}
	return result
}

// toEntry converts [id, [field, value, ...]]. A trimmed entry in a pending-history read arrives as
// [id, nil] and keeps nil Fields, which toMap would otherwise turn into an empty map.
func toEntry(reply any) (StreamEntry, bool) {
	parts, ok := reply.([]any)
	if !ok || len(parts) != 2 {
		return StreamEntry{}, false
	}
	id, _ := parts[0].(string)
	if parts[1] == nil {
		return StreamEntry{ID: id}, true
	}
	return StreamEntry{ID: id, Fields: toMap(parts[1])}, true
}

func toEntries(reply any) []StreamEntry {
	rows, _ := reply.([]any)
	entries := make([]StreamEntry, 0, len(rows))
	for _, row := range rows {
		if entry, ok := toEntry(row); ok {
			entries = append(entries, entry)
		}
	}
	return entries
}

// toStreamReply converts [[key, [entry...]], ...] into a map. A nil reply stays a nil map.
func toStreamReply(reply any, err error) (map[string][]StreamEntry, error) {
	if err != nil || reply == nil {
		return nil, err
	}

	streams, _ := reply.([]any)
	result := make(map[string][]StreamEntry, len(streams))
	for _, stream := range streams {
		parts, ok := stream.([]any)
		if !ok || len(parts) != 2 {
			continue
		}
		key, _ := parts[0].(string)
		result[key] = toEntries(parts[1])
	}
	return result, nil
}
