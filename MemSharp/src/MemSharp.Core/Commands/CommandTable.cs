using System.Globalization;
using MemSharp.Collections;
using MemSharp.Protocol;

namespace MemSharp.Commands;

/// <summary>Everything a command handler needs beyond the database itself.</summary>
/// <param name="Db">The database.</param>
/// <param name="Session">
/// The calling connection, for commands that affect it (<c>SUBSCRIBE</c>, <c>SELECT</c>). Null when
/// the command comes from the append-only log or an embedded caller.
/// </param>
public readonly record struct CommandContext(MemDb Db, ICommandSession? Session);

/// <summary>The per-connection state a command may touch.</summary>
public interface ICommandSession
{
    /// <summary>Starts a subscription that pushes to this connection.</summary>
    void AddSubscription(string channelOrPattern, bool isPattern);

    /// <summary>Ends one subscription, or all of them when <paramref name="channelOrPattern"/> is null.</summary>
    void RemoveSubscription(string? channelOrPattern, bool isPattern);

    /// <summary>Channels and patterns this connection is currently subscribed to.</summary>
    int SubscriptionCount { get; }

    /// <summary>Asks the server to close this connection after the current reply is flushed.</summary>
    void RequestClose();
}

/// <summary>Metadata plus the handler for one command.</summary>
/// <param name="Name">Upper-case command name.</param>
/// <param name="Arity">
/// Minimum argument count including the command name itself. Negative means "at least that many".
/// </param>
/// <param name="IsWrite">True if the command mutates the keyspace.</param>
/// <param name="Handler">Executes the command.</param>
/// <param name="Summary">One-line description, surfaced by <c>COMMAND DOCS</c> and the CLI.</param>
public sealed record CommandDefinition(
    string Name,
    int Arity,
    bool IsWrite,
    Func<CommandContext, string[], RespValue> Handler,
    string Summary);

/// <summary>
/// The single dispatch table: command name to handler.
/// </summary>
/// <remarks>
/// The server, the append-only log replay and the CLI all go through this one table. When they had
/// separate switch statements - as the original engine's server did - a command added to one of
/// them silently failed to replay from disk, which is the sort of divergence that only shows up
/// after a restart with real data in it.
/// </remarks>
public static class CommandTable
{
    private static readonly Dictionary<string, CommandDefinition> Commands = Build();

    /// <summary>Every command, ordered by name.</summary>
    public static IEnumerable<CommandDefinition> All => Commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal);

    /// <summary>Looks up a command by name, case-insensitively.</summary>
    public static bool TryGet(string name, out CommandDefinition definition) =>
        Commands.TryGetValue(name.ToUpperInvariant(), out definition!);

    /// <summary>
    /// Executes an argument vector, argument 0 being the command name. Errors that are part of
    /// normal operation come back as an error reply rather than an exception.
    /// </summary>
    public static RespValue Execute(CommandContext context, string[] arguments)
    {
        if (arguments.Length == 0) return RespValue.Error("ERR", "empty command");

        if (!TryGet(arguments[0], out var definition))
        {
            return RespValue.Error("ERR", $"unknown command '{arguments[0]}'");
        }

        if (definition.Arity >= 0 ? arguments.Length != definition.Arity : arguments.Length < -definition.Arity)
        {
            return RespValue.Error("ERR", $"wrong number of arguments for '{definition.Name}'");
        }

        try
        {
            return definition.Handler(context, arguments);
        }
        catch (MemSharpException ex)
        {
            return RespValue.Error(ex.Code, ex.Message);
        }
        catch (FormatException)
        {
            return RespValue.Error("ERR", "value is not an integer or out of range");
        }
        catch (OverflowException)
        {
            return RespValue.Error("ERR", "value is out of range");
        }
    }

    private static Dictionary<string, CommandDefinition> Build()
    {
        var table = new Dictionary<string, CommandDefinition>(StringComparer.Ordinal);

        void Add(string name, int arity, bool isWrite, Func<CommandContext, string[], RespValue> handler, string summary)
            => table[name] = new CommandDefinition(name, arity, isWrite, handler, summary);

        // ---- connection and server ----------------------------------------------------------
        Add("PING", -1, false, static (_, a) => a.Length > 1 ? RespValue.Bulk(a[1]) : RespValue.Status("PONG"),
            "Returns PONG, or echoes its argument.");
        Add("ECHO", 2, false, static (_, a) => RespValue.Bulk(a[1]),
            "Returns its argument unchanged.");
        Add("QUIT", 1, false, static (c, _) => { c.Session?.RequestClose(); return RespValue.Ok; },
            "Closes the connection.");
        Add("DBSIZE", 1, false, static (c, _) => RespValue.Number64(c.Db.Count),
            "Number of keys in the database.");
        Add("FLUSHDB", 1, true, static (c, _) => { c.Db.Clear(); return RespValue.Ok; },
            "Removes every key.");
        Add("INFO", -1, false, static (c, _) => RespValue.Bulk(BuildInfo(c.Db)),
            "Server statistics as a text block.");
        Add("COMMAND", -1, false, static (_, _) => RespValue.BulkArray(All.Select(c => (string?)c.Name)),
            "Lists every supported command.");

        // ---- persistence ---------------------------------------------------------------------
        Add("SAVE", 1, false, static (c, _) => { c.Db.Save(); return RespValue.Ok; },
            "Writes a snapshot synchronously.");
        Add("BGSAVE", 1, false, static (c, _) =>
        {
            // Fire and forget: the reply says the save started, not that it finished, which is the
            // whole point of BGSAVE. The continuation observes any fault so it does not surface as
            // an unobserved task exception; failures show up in the next LASTSAVE.
            var saving = c.Db.SaveAsync();
            saving.ContinueWith(static t => t.Exception?.Handle(static _ => true), TaskScheduler.Default);
            return RespValue.Status("Background saving started");
        }, "Writes a snapshot in the background.");
        Add("LASTSAVE", 1, false, static (c, _) => RespValue.Number64(
            c.Db.LastSaveTime?.ToUnixTimeSeconds() ?? 0),
            "Unix time of the last successful snapshot.");

        // ---- keyspace --------------------------------------------------------------------------
        Add("DEL", -2, true, static (c, a) => RespValue.Number64(c.Db.Delete(a[1..])),
            "Removes keys; returns how many existed.");
        Add("EXISTS", -2, false, static (c, a) =>
        {
            int found = 0;
            for (int i = 1; i < a.Length; i++)
            {
                if (c.Db.ContainsKey(a[i])) found++;
            }
            return RespValue.Number64(found);
        }, "Counts how many of the given keys exist.");
        Add("TYPE", 2, false, static (c, a) =>
        {
            var type = c.Db.TypeOf(a[1]);
            return RespValue.Status(type == MemType.None ? "none" : type.ToString().ToLowerInvariant());
        }, "The type of value held at a key.");
        Add("KEYS", 2, false, static (c, a) => RespValue.BulkArray(c.Db.Keys(a[1]).Select(k => (string?)k)),
            "Keys matching a glob pattern.");
        Add("SCAN", -2, false, static (c, a) =>
        {
            string pattern = "*";
            int count = 100;
            for (int i = 2; i + 1 < a.Length; i += 2)
            {
                if (a[i].Equals("MATCH", StringComparison.OrdinalIgnoreCase)) pattern = a[i + 1];
                else if (a[i].Equals("COUNT", StringComparison.OrdinalIgnoreCase)) count = ParseInt(a[i + 1]);
            }

            // A real cursor would have to survive rehashing across shards. MemSharp instead treats
            // the cursor as an offset into a stable scan order and returns 0 when the page is short,
            // which gives clients the usual "loop until the cursor is 0" contract.
            int offset = ParseInt(a[1]);
            var page = c.Db.Scan(pattern).Skip(offset).Take(count).ToList();
            long next = page.Count < count ? 0 : offset + page.Count;
            return RespValue.Array(
                RespValue.Bulk(next.ToString(CultureInfo.InvariantCulture)),
                RespValue.BulkArray(page.Select(k => (string?)k)));
        }, "Incrementally iterates the keyspace.");
        Add("RENAME", 3, true, static (c, a) => c.Db.Rename(a[1], a[2])
            ? RespValue.Ok
            : RespValue.Error("ERR", "no such key"),
            "Renames a key, overwriting the destination.");
        Add("RANDOMKEY", 1, false, static (c, _) => RespValue.Bulk(c.Db.RandomKey()),
            "Returns a random key.");

        Add("EXPIRE", 3, true, static (c, a) => RespValue.Boolean(
            c.Db.Expire(a[1], TimeSpan.FromSeconds(ParseLong(a[2])))),
            "Sets a key's lifetime in seconds.");
        Add("PEXPIRE", 3, true, static (c, a) => RespValue.Boolean(
            c.Db.Expire(a[1], TimeSpan.FromMilliseconds(ParseLong(a[2])))),
            "Sets a key's lifetime in milliseconds.");
        Add("PEXPIREAT", 3, true, static (c, a) => RespValue.Boolean(
            c.Db.ExpireAt(a[1], DateTimeOffset.FromUnixTimeMilliseconds(ParseLong(a[2])))),
            "Sets a key's absolute expiry.");
        Add("TTL", 2, false, static (c, a) =>
        {
            if (!c.Db.ContainsKey(a[1])) return RespValue.Number64(-2);
            var ttl = c.Db.TimeToLive(a[1]);
            return RespValue.Number64(ttl is null ? -1 : (long)ttl.Value.TotalSeconds);
        }, "Remaining lifetime in seconds; -1 if permanent, -2 if absent.");
        Add("PTTL", 2, false, static (c, a) =>
        {
            if (!c.Db.ContainsKey(a[1])) return RespValue.Number64(-2);
            var ttl = c.Db.TimeToLive(a[1]);
            return RespValue.Number64(ttl is null ? -1 : (long)ttl.Value.TotalMilliseconds);
        }, "Remaining lifetime in milliseconds.");
        Add("PERSIST", 2, true, static (c, a) => RespValue.Boolean(c.Db.Persist(a[1])),
            "Clears a key's expiry.");

        // ---- strings ---------------------------------------------------------------------------
        Add("SET", -3, true, static (c, a) =>
        {
            TimeSpan? ttl = null;
            bool onlyIfAbsent = false;

            for (int i = 3; i < a.Length; i++)
            {
                if (a[i].Equals("EX", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Length)
                {
                    ttl = TimeSpan.FromSeconds(ParseLong(a[++i]));
                }
                else if (a[i].Equals("PX", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Length)
                {
                    ttl = TimeSpan.FromMilliseconds(ParseLong(a[++i]));
                }
                else if (a[i].Equals("NX", StringComparison.OrdinalIgnoreCase))
                {
                    onlyIfAbsent = true;
                }
            }

            if (onlyIfAbsent) return c.Db.SetIfAbsent(a[1], a[2], ttl) ? RespValue.Ok : RespValue.Null;
            c.Db.Set(a[1], a[2], ttl);
            return RespValue.Ok;
        }, "Stores a string, optionally with EX/PX seconds or milliseconds and NX.");
        Add("SETNX", 3, true, static (c, a) => RespValue.Boolean(c.Db.SetIfAbsent(a[1], a[2])),
            "Stores a string only if the key is absent.");
        Add("GET", 2, false, static (c, a) => RespValue.Bulk(c.Db.Get(a[1])),
            "Reads a string.");
        Add("GETSET", 3, true, static (c, a) => RespValue.Bulk(c.Db.GetSet(a[1], a[2])),
            "Replaces a string and returns the old value.");
        Add("MGET", -2, false, static (c, a) => RespValue.BulkArray(c.Db.GetMany(a[1..])),
            "Reads several strings.");
        Add("MSET", -3, true, static (c, a) =>
        {
            for (int i = 1; i + 1 < a.Length; i += 2) c.Db.Set(a[i], a[i + 1]);
            return RespValue.Ok;
        }, "Stores several key/value pairs.");
        Add("INCR", 2, true, static (c, a) => RespValue.Number64(c.Db.Increment(a[1])),
            "Adds one to an integer.");
        Add("DECR", 2, true, static (c, a) => RespValue.Number64(c.Db.Increment(a[1], -1)),
            "Subtracts one from an integer.");
        Add("INCRBY", 3, true, static (c, a) => RespValue.Number64(c.Db.Increment(a[1], ParseLong(a[2]))),
            "Adds an amount to an integer.");
        Add("DECRBY", 3, true, static (c, a) => RespValue.Number64(c.Db.Increment(a[1], -ParseLong(a[2]))),
            "Subtracts an amount from an integer.");
        Add("INCRBYFLOAT", 3, true, static (c, a) => RespValue.Float(c.Db.IncrementByFloat(a[1], ParseDouble(a[2]))),
            "Adds an amount to a floating-point number.");
        Add("APPEND", 3, true, static (c, a) => RespValue.Number64(c.Db.Append(a[1], a[2])),
            "Appends to a string; returns the new length.");
        Add("STRLEN", 2, false, static (c, a) => RespValue.Number64(c.Db.StringLength(a[1])),
            "Length of a string.");

        // ---- lists ------------------------------------------------------------------------------
        Add("LPUSH", -3, true, static (c, a) => RespValue.Number64(c.Db.ListPushLeft(a[1], a[2..])),
            "Pushes values onto the head of a list.");
        Add("RPUSH", -3, true, static (c, a) => RespValue.Number64(c.Db.ListPushRight(a[1], a[2..])),
            "Pushes values onto the tail of a list.");
        Add("LPOP", 2, true, static (c, a) => RespValue.Bulk(c.Db.ListPopLeft(a[1])),
            "Removes and returns the head of a list.");
        Add("RPOP", 2, true, static (c, a) => RespValue.Bulk(c.Db.ListPopRight(a[1])),
            "Removes and returns the tail of a list.");
        Add("LRANGE", 4, false, static (c, a) => RespValue.BulkArray(
            c.Db.ListRange(a[1], ParseInt(a[2]), ParseInt(a[3])).Select(v => (string?)v)),
            "Elements of a list in an index range.");
        Add("LLEN", 2, false, static (c, a) => RespValue.Number64(c.Db.ListLength(a[1])),
            "Length of a list.");
        Add("LINDEX", 3, false, static (c, a) => RespValue.Bulk(c.Db.ListIndex(a[1], ParseInt(a[2]))),
            "The element at an index.");
        Add("LSET", 4, true, static (c, a) => c.Db.ListSet(a[1], ParseInt(a[2]), a[3])
            ? RespValue.Ok
            : RespValue.Error("ERR", "index out of range"),
            "Overwrites the element at an index.");
        Add("LTRIM", 4, true, static (c, a) => { c.Db.ListTrim(a[1], ParseInt(a[2]), ParseInt(a[3])); return RespValue.Ok; },
            "Keeps only an index range of a list.");
        Add("LREM", 4, true, static (c, a) => RespValue.Number64(c.Db.ListRemove(a[1], a[3], ParseInt(a[2]))),
            "Removes occurrences of a value from a list.");
        Add("RPOPLPUSH", 3, true, static (c, a) => RespValue.Bulk(c.Db.ListMove(a[1], a[2])),
            "Moves the tail of one list onto the head of another.");

        // ---- hashes -----------------------------------------------------------------------------
        Add("HSET", -4, true, static (c, a) =>
        {
            var pairs = new List<KeyValuePair<string, string>>();
            for (int i = 2; i + 1 < a.Length; i += 2) pairs.Add(new KeyValuePair<string, string>(a[i], a[i + 1]));
            return RespValue.Number64(c.Db.HashSetMany(a[1], pairs));
        }, "Sets one or more fields of a hash.");
        Add("HGET", 3, false, static (c, a) => RespValue.Bulk(c.Db.HashGet(a[1], a[2])),
            "Reads a field of a hash.");
        Add("HMGET", -3, false, static (c, a) => RespValue.BulkArray(c.Db.HashGetMany(a[1], a[2..])),
            "Reads several fields of a hash.");
        Add("HGETALL", 2, false, static (c, a) =>
        {
            var hash = c.Db.HashGetAll(a[1]);
            var items = new List<RespValue>(hash.Count * 2);
            foreach (var pair in hash)
            {
                items.Add(RespValue.Bulk(pair.Key));
                items.Add(RespValue.Bulk(pair.Value));
            }
            return RespValue.Array(items.ToArray());
        }, "Every field and value of a hash, flattened.");
        Add("HDEL", -3, true, static (c, a) => RespValue.Number64(c.Db.HashDelete(a[1], a[2..])),
            "Removes fields from a hash.");
        Add("HEXISTS", 3, false, static (c, a) => RespValue.Boolean(c.Db.HashContains(a[1], a[2])),
            "True if a hash has a field.");
        Add("HLEN", 2, false, static (c, a) => RespValue.Number64(c.Db.HashLength(a[1])),
            "Number of fields in a hash.");
        Add("HKEYS", 2, false, static (c, a) => RespValue.BulkArray(c.Db.HashKeys(a[1]).Select(v => (string?)v)),
            "Field names of a hash.");
        Add("HVALS", 2, false, static (c, a) => RespValue.BulkArray(c.Db.HashValues(a[1]).Select(v => (string?)v)),
            "Values of a hash.");
        Add("HINCRBY", 4, true, static (c, a) => RespValue.Number64(c.Db.HashIncrement(a[1], a[2], ParseLong(a[3]))),
            "Adds an amount to an integer field.");
        Add("HINCRBYFLOAT", 4, true, static (c, a) => RespValue.Float(
            c.Db.HashIncrementByFloat(a[1], a[2], ParseDouble(a[3]))),
            "Adds an amount to a floating-point field.");

        // ---- sets --------------------------------------------------------------------------------
        Add("SADD", -3, true, static (c, a) => RespValue.Number64(c.Db.SetAdd(a[1], a[2..])),
            "Adds members to a set.");
        Add("SREM", -3, true, static (c, a) => RespValue.Number64(c.Db.SetRemove(a[1], a[2..])),
            "Removes members from a set.");
        Add("SMEMBERS", 2, false, static (c, a) => RespValue.BulkArray(c.Db.SetMembers(a[1]).Select(v => (string?)v)),
            "Every member of a set.");
        Add("SISMEMBER", 3, false, static (c, a) => RespValue.Boolean(c.Db.SetContains(a[1], a[2])),
            "True if a set contains a member.");
        Add("SCARD", 2, false, static (c, a) => RespValue.Number64(c.Db.SetLength(a[1])),
            "Number of members in a set.");
        Add("SPOP", 2, true, static (c, a) => RespValue.Bulk(c.Db.SetPop(a[1])),
            "Removes and returns an arbitrary member.");
        Add("SINTER", -2, false, static (c, a) => RespValue.BulkArray(c.Db.SetIntersect(a[1..]).Select(v => (string?)v)),
            "Members present in every named set.");
        Add("SUNION", -2, false, static (c, a) => RespValue.BulkArray(c.Db.SetUnion(a[1..]).Select(v => (string?)v)),
            "Members present in any named set.");
        Add("SDIFF", -2, false, static (c, a) => RespValue.BulkArray(c.Db.SetDifference(a[1..]).Select(v => (string?)v)),
            "Members of the first set absent from the others.");

        // ---- sorted sets ---------------------------------------------------------------------------
        Add("ZADD", -4, true, static (c, a) =>
        {
            // Arguments alternate score then member, starting after the key.
            var members = new List<ScoredMember>();
            for (int i = 2; i + 1 < a.Length; i += 2) members.Add(new ScoredMember(a[i + 1], ParseDouble(a[i])));
            return RespValue.Number64(c.Db.SortedSetAdd(a[1], members));
        }, "Adds scored members to a sorted set.");
        Add("ZREM", -3, true, static (c, a) => RespValue.Number64(c.Db.SortedSetRemove(a[1], a[2..])),
            "Removes members from a sorted set.");
        Add("ZSCORE", 3, false, static (c, a) =>
        {
            double? score = c.Db.SortedSetScore(a[1], a[2]);
            return score is null ? RespValue.Null : RespValue.Float(score.Value);
        }, "The score of a member.");
        Add("ZINCRBY", 4, true, static (c, a) => RespValue.Float(
            c.Db.SortedSetIncrement(a[1], a[3], ParseDouble(a[2]))),
            "Adds an amount to a member's score.");
        Add("ZCARD", 2, false, static (c, a) => RespValue.Number64(c.Db.SortedSetLength(a[1])),
            "Number of members in a sorted set.");
        Add("ZRANK", 3, false, static (c, a) =>
        {
            int? rank = c.Db.SortedSetRank(a[1], a[2]);
            return rank is null ? RespValue.Null : RespValue.Number64(rank.Value);
        }, "Rank of a member, lowest score first.");
        Add("ZREVRANK", 3, false, static (c, a) =>
        {
            int? rank = c.Db.SortedSetRank(a[1], a[2], descending: true);
            return rank is null ? RespValue.Null : RespValue.Number64(rank.Value);
        }, "Rank of a member, highest score first.");
        Add("ZRANGE", -4, false, static (c, a) => RenderScored(
            c.Db.SortedSetRangeByRank(a[1], ParseInt(a[2]), ParseInt(a[3])), HasWithScores(a)),
            "Members in a rank range.");
        Add("ZREVRANGE", -4, false, static (c, a) => RenderScored(
            c.Db.SortedSetRangeByRank(a[1], ParseInt(a[2]), ParseInt(a[3]), descending: true), HasWithScores(a)),
            "Members in a rank range, highest score first.");
        Add("ZRANGEBYSCORE", -4, false, static (c, a) => RenderScored(
            c.Db.SortedSetRangeByScore(a[1], ParseScoreBound(a[2]), ParseScoreBound(a[3])), HasWithScores(a)),
            "Members in a score range.");
        Add("ZREVRANGEBYSCORE", -4, false, static (c, a) => RenderScored(
            c.Db.SortedSetRangeByScore(a[1], ParseScoreBound(a[3]), ParseScoreBound(a[2]), descending: true), HasWithScores(a)),
            "Members in a score range, highest score first.");
        Add("ZCOUNT", 4, false, static (c, a) => RespValue.Number64(
            c.Db.SortedSetCountByScore(a[1], ParseScoreBound(a[2]), ParseScoreBound(a[3]))),
            "Number of members in a score range.");
        Add("ZREMRANGEBYSCORE", 4, true, static (c, a) => RespValue.Number64(
            c.Db.SortedSetRemoveByScore(a[1], ParseScoreBound(a[2]), ParseScoreBound(a[3]))),
            "Removes members in a score range.");

        // ---- time series ------------------------------------------------------------------------------
        Add("TS.CREATE", -2, true, static (c, a) =>
        {
            int retention = 0;
            for (int i = 2; i + 1 < a.Length; i += 2)
            {
                if (a[i].Equals("RETENTION", StringComparison.OrdinalIgnoreCase)) retention = ParseInt(a[i + 1]);
            }
            c.Db.TimeSeriesCreate(a[1], retention);
            return RespValue.Ok;
        }, "Creates a time series, optionally capped at RETENTION samples.");
        Add("TS.ADD", -3, true, static (c, a) =>
        {
            // '*' means "stamp it with the current time", matching the stream convention.
            long? timestamp = a.Length > 3 && a[2] != "*" ? ParseLong(a[2]) : null;
            double value = ParseDouble(a[^1]);
            return RespValue.Number64(c.Db.TimeSeriesAdd(a[1], value, timestamp));
        }, "Appends a sample to a time series.");
        Add("TS.RANGE", 4, false, static (c, a) =>
        {
            var samples = c.Db.TimeSeriesRange(a[1], ParseTimeBound(a[2], long.MinValue), ParseTimeBound(a[3], long.MaxValue));
            return RespValue.Array(samples.Select(s => RespValue.Array(
                RespValue.Number64(s.Timestamp), RespValue.Float(s.Value))).ToArray());
        }, "Samples in a timestamp range.");
        Add("TS.AGGREGATE", 6, false, static (c, a) =>
        {
            var aggregation = a[5].ToUpperInvariant() switch
            {
                "AVG" or "AVERAGE" => TimeSeriesAggregation.Average,
                "MIN" => TimeSeriesAggregation.Min,
                "MAX" => TimeSeriesAggregation.Max,
                "SUM" => TimeSeriesAggregation.Sum,
                "COUNT" => TimeSeriesAggregation.Count,
                "FIRST" => TimeSeriesAggregation.First,
                "LAST" => TimeSeriesAggregation.Last,
                _ => throw new MemSharpCommandException($"unknown aggregation '{a[5]}'"),
            };
            var buckets = c.Db.TimeSeriesAggregate(
                a[1], ParseTimeBound(a[2], long.MinValue), ParseTimeBound(a[3], long.MaxValue), ParseLong(a[4]), aggregation);
            return RespValue.Array(buckets.Select(s => RespValue.Array(
                RespValue.Number64(s.Timestamp), RespValue.Float(s.Value))).ToArray());
        }, "Folds a time series range into fixed-width buckets.");
        Add("TS.LEN", 2, false, static (c, a) => RespValue.Number64(c.Db.TimeSeriesLength(a[1])),
            "Number of samples in a time series.");

        // ---- streams -----------------------------------------------------------------------------------
        Add("XADD", -5, true, static (c, a) =>
        {
            int cursor = 2;
            int maxLength = 0;
            if (a[cursor].Equals("MAXLEN", StringComparison.OrdinalIgnoreCase))
            {
                cursor++;
                if (a[cursor] == "~") cursor++;   // approximate trimming; MemSharp trims exactly
                maxLength = ParseInt(a[cursor++]);
            }

            StreamId? id = a[cursor] == "*" ? null : ParseStreamId(a[cursor], 0);
            cursor++;

            var fields = a[cursor..];
            if (fields.Length == 0 || fields.Length % 2 != 0)
            {
                return RespValue.Error("ERR", "stream fields must come in name/value pairs");
            }
            return RespValue.Bulk(c.Db.StreamAdd(a[1], fields, id, maxLength).ToString());
        }, "Appends an entry to a stream.");
        Add("XLEN", 2, false, static (c, a) => RespValue.Number64(c.Db.StreamLength(a[1])),
            "Number of entries in a stream.");
        Add("XRANGE", -4, false, static (c, a) =>
        {
            int limit = -1;
            for (int i = 4; i + 1 < a.Length; i += 2)
            {
                if (a[i].Equals("COUNT", StringComparison.OrdinalIgnoreCase)) limit = ParseInt(a[i + 1]);
            }
            var entries = c.Db.StreamRange(a[1], ParseStreamBound(a[2], StreamId.Min), ParseStreamBound(a[3], StreamId.Max), false, limit);
            return RenderStream(entries);
        }, "Entries of a stream in an id range.");
        Add("XREVRANGE", -4, false, static (c, a) =>
        {
            var entries = c.Db.StreamRange(a[1], ParseStreamBound(a[3], StreamId.Min), ParseStreamBound(a[2], StreamId.Max), true, -1);
            return RenderStream(entries);
        }, "Entries of a stream, newest first.");
        Add("XTRIM", -4, true, static (c, a) => RespValue.Number64(c.Db.StreamTrim(a[1], ParseInt(a[^1]))),
            "Caps a stream at MAXLEN entries.");

        // ---- pub/sub -------------------------------------------------------------------------------------
        Add("PUBLISH", 3, false, static (c, a) => RespValue.Number64(c.Db.Publish(a[1], a[2])),
            "Publishes a message; returns the number of receivers.");
        Add("SUBSCRIBE", -2, false, static (c, a) =>
        {
            if (c.Session is null) return RespValue.Error("ERR", "SUBSCRIBE requires a connection");
            for (int i = 1; i < a.Length; i++) c.Session.AddSubscription(a[i], isPattern: false);
            return RespValue.Array(
                RespValue.Bulk("subscribe"), RespValue.Bulk(a[^1]), RespValue.Number64(c.Session.SubscriptionCount));
        }, "Subscribes the connection to channels.");
        Add("PSUBSCRIBE", -2, false, static (c, a) =>
        {
            if (c.Session is null) return RespValue.Error("ERR", "PSUBSCRIBE requires a connection");
            for (int i = 1; i < a.Length; i++) c.Session.AddSubscription(a[i], isPattern: true);
            return RespValue.Array(
                RespValue.Bulk("psubscribe"), RespValue.Bulk(a[^1]), RespValue.Number64(c.Session.SubscriptionCount));
        }, "Subscribes the connection to channel patterns.");
        Add("UNSUBSCRIBE", -1, false, static (c, a) =>
        {
            if (c.Session is null) return RespValue.Error("ERR", "UNSUBSCRIBE requires a connection");
            if (a.Length == 1) c.Session.RemoveSubscription(null, isPattern: false);
            else for (int i = 1; i < a.Length; i++) c.Session.RemoveSubscription(a[i], isPattern: false);
            return RespValue.Array(
                RespValue.Bulk("unsubscribe"), RespValue.Bulk(a.Length > 1 ? a[^1] : null), RespValue.Number64(c.Session.SubscriptionCount));
        }, "Unsubscribes the connection from channels.");
        Add("PUNSUBSCRIBE", -1, false, static (c, a) =>
        {
            if (c.Session is null) return RespValue.Error("ERR", "PUNSUBSCRIBE requires a connection");
            if (a.Length == 1) c.Session.RemoveSubscription(null, isPattern: true);
            else for (int i = 1; i < a.Length; i++) c.Session.RemoveSubscription(a[i], isPattern: true);
            return RespValue.Array(
                RespValue.Bulk("punsubscribe"), RespValue.Bulk(a.Length > 1 ? a[^1] : null), RespValue.Number64(c.Session.SubscriptionCount));
        }, "Unsubscribes the connection from channel patterns.");

        // ---- query ---------------------------------------------------------------------------------------
        Add("SQL", -2, true, static (c, a) =>
        {
            string sql = string.Join(' ', a, 1, a.Length - 1);
            var result = c.Db.ExecuteSql(sql);

            if (result.Columns.Count == 0) return RespValue.Number64(result.Affected);

            var rows = new List<RespValue>(result.Rows.Count + 1)
            {
                RespValue.BulkArray(result.Columns.Select(x => (string?)x)),
            };
            foreach (var row in result.Rows) rows.Add(RespValue.BulkArray(row));
            return RespValue.Array(rows.ToArray());
        }, "Runs a SELECT or DELETE against the keyspace.");

        return table;
    }

    private static bool HasWithScores(string[] arguments)
    {
        for (int i = 4; i < arguments.Length; i++)
        {
            if (arguments[i].Equals("WITHSCORES", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static RespValue RenderScored(List<ScoredMember> members, bool withScores)
    {
        if (!withScores) return RespValue.BulkArray(members.Select(m => (string?)m.Member));

        var items = new List<RespValue>(members.Count * 2);
        foreach (var member in members)
        {
            items.Add(RespValue.Bulk(member.Member));
            items.Add(RespValue.Bulk(member.Score.ToString("R", CultureInfo.InvariantCulture)));
        }
        return RespValue.Array(items.ToArray());
    }

    private static RespValue RenderStream(List<StreamEntry> entries)
    {
        var items = new RespValue[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            items[i] = RespValue.Array(
                RespValue.Bulk(entries[i].Id.ToString()),
                RespValue.BulkArray(entries[i].Fields.Select(f => (string?)f)));
        }
        return RespValue.Array(items);
    }

    private static string BuildInfo(MemDb db)
    {
        var stats = db.Statistics.Snapshot();
        return $"""
            # Server
            product:MemSharp
            vendor:Gravicode Studios
            version:{typeof(MemDb).Assembly.GetName().Version}

            # Keyspace
            keys:{db.Count}
            shards:{db.ShardCount}

            # Stats
            uptime_seconds:{(long)stats.Uptime.TotalSeconds}
            commands_processed:{stats.CommandsProcessed}
            connections_accepted:{stats.ConnectionsAccepted}
            keyspace_hits:{stats.Hits}
            keyspace_misses:{stats.Misses}
            hit_rate:{stats.HitRate:F4}
            writes:{stats.Writes}
            expired_keys:{stats.ExpiredKeys}
            pubsub_messages:{stats.MessagesDelivered}

            # Persistence
            last_save:{db.LastSaveTime?.ToUnixTimeSeconds() ?? 0}
            pending_changes:{db.PendingChanges}
            """;
    }

    private static int ParseInt(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value
        : throw new NotANumberException($"'{text}' is not an integer");

    private static long ParseLong(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
        ? value
        : throw new NotANumberException($"'{text}' is not an integer");

    private static double ParseDouble(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        ? value
        : throw new NotANumberException($"'{text}' is not a number");

    /// <summary>Parses a score bound, accepting the Redis infinities <c>-inf</c> and <c>+inf</c>.</summary>
    private static double ParseScoreBound(string text) => text switch
    {
        "-inf" or "-INF" => double.NegativeInfinity,
        "+inf" or "inf" or "+INF" or "INF" => double.PositiveInfinity,
        _ => ParseDouble(text),
    };

    /// <summary>Parses a timestamp bound, accepting <c>-</c> and <c>+</c> for the ends of the series.</summary>
    private static long ParseTimeBound(string text, long fallback) => text switch
    {
        "-" or "+" => fallback,
        _ => ParseLong(text),
    };

    private static StreamId ParseStreamId(string text, long defaultSequence) =>
        StreamId.TryParse(text, defaultSequence, out var id) ? id : throw new MemSharpCommandException($"invalid stream id '{text}'");

    private static StreamId ParseStreamBound(string text, StreamId fallback) => text switch
    {
        "-" => StreamId.Min,
        "+" => StreamId.Max,
        _ => ParseStreamId(text, fallback.Sequence),
    };
}
