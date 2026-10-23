using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using MemSharp.Collections;
using MemSharp.Persistence;
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
        Add("INFO", -1, false, static (c, a) => RespValue.Bulk(BuildInfo(c.Db, a.Length > 1 ? a[1..] : [])),
            "Server statistics as a text block, optionally limited to sections.");
        Add("CONFIG", -2, false, static (c, a) => a[1].ToUpperInvariant() switch
        {
            "GET" when a.Length >= 3 => ConfigGet(c.Db, a[2..]),
            "RESETSTAT" => ResetStatistics(c.Db),
            "SET" or "REWRITE" => RespValue.Error("ERR",
                $"CONFIG {a[1].ToUpperInvariant()} is not supported: MemSharp is configured through MemDbOptions when it is constructed"),
            _ => RespValue.Error("ERR", $"unknown CONFIG subcommand '{a[1]}', or wrong number of arguments"),
        }, "Reads configuration (GET), or resets statistics (RESETSTAT).");
        Add("MEMORY", -2, false, static (c, a) =>
        {
            if (!a[1].Equals("USAGE", StringComparison.OrdinalIgnoreCase) || a.Length < 3)
            {
                return RespValue.Error("ERR", $"unknown MEMORY subcommand '{a[1]}': MemSharp supports MEMORY USAGE key [SAMPLES n]");
            }
            int samples = 5;
            for (int i = 3; i + 1 < a.Length; i += 2)
            {
                if (a[i].Equals("SAMPLES", StringComparison.OrdinalIgnoreCase)) samples = ParseInt(a[i + 1]);
            }
            long? bytes = c.Db.MemoryUsage(a[2], samples);
            return bytes is null ? RespValue.Null : RespValue.Number64(bytes.Value);
        }, "Estimated bytes held by a key: MEMORY USAGE key [SAMPLES n].");
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

            // The cursor encodes a shard and a hash threshold, so a key present for the whole scan is
            // returned however the keyspace changes in between - see MemDb.ScanPage.
            if (!long.TryParse(a[1], NumberStyles.None, CultureInfo.InvariantCulture, out long cursor))
            {
                return RespValue.Error("ERR", "invalid cursor");
            }
            var page = c.Db.ScanPage(cursor, pattern, count);
            return RespValue.Array(
                RespValue.Bulk(page.Cursor.ToString(CultureInfo.InvariantCulture)),
                RespValue.BulkArray(page.Keys.Select(k => (string?)k)));
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
            "Length of a string in UTF-8 bytes.");
        Add("GETRANGE", 4, false, static (c, a) => RespValue.Bulk(c.Db.GetRange(a[1], ParseInt(a[2]), ParseInt(a[3]))),
            "The substring between two inclusive byte offsets.");
        Add("SETRANGE", 4, true, static (c, a) => RespValue.Number64(c.Db.SetRange(a[1], ParseInt(a[2]), a[3])),
            "Overwrites part of a string from a byte offset; returns the new length.");

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
        Add("LPOS", -3, false, static (c, a) =>
        {
            int rank = 1, maxLength = 0;
            int? count = null;
            for (int i = 3; i < a.Length; i += 2)
            {
                if (i + 1 >= a.Length) return RespValue.Error("ERR", "syntax error");
                switch (a[i].ToUpperInvariant())
                {
                    case "RANK": rank = ParseInt(a[i + 1]); break;
                    case "COUNT": count = ParseInt(a[i + 1]); break;
                    case "MAXLEN": maxLength = ParseInt(a[i + 1]); break;
                    default: return RespValue.Error("ERR", "syntax error");
                }
            }

            // Without COUNT the reply is one index or nil; with it, always an array.
            if (count is null)
            {
                int? position = c.Db.ListPosition(a[1], a[2], rank, maxLength);
                return position is null ? RespValue.Null : RespValue.Number64(position.Value);
            }
            var positions = c.Db.ListPositions(a[1], a[2], count.Value, rank, maxLength);
            return RespValue.Array(positions.Select(p => RespValue.Number64(p)).ToArray());
        }, "Index of matching elements: LPOS key element [RANK r] [COUNT n] [MAXLEN m].");

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
        Add("SINTERSTORE", -3, true, static (c, a) => RespValue.Number64(c.Db.SetIntersectStore(a[1], a[2..])),
            "Stores the intersection of sets; returns its size.");
        Add("SUNIONSTORE", -3, true, static (c, a) => RespValue.Number64(c.Db.SetUnionStore(a[1], a[2..])),
            "Stores the union of sets; returns its size.");
        Add("SDIFFSTORE", -3, true, static (c, a) => RespValue.Number64(c.Db.SetDifferenceStore(a[1], a[2..])),
            "Stores the first set minus the others; returns its size.");

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
        Add("ZUNIONSTORE", -4, true, static (c, a) => SortedStore(c, a, intersect: false),
            "Stores the union of sorted sets: ZUNIONSTORE dest numkeys key... [WEIGHTS w...] [AGGREGATE SUM|MIN|MAX].");
        Add("ZINTERSTORE", -4, true, static (c, a) => SortedStore(c, a, intersect: true),
            "Stores the intersection of sorted sets, with the same options as ZUNIONSTORE.");

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
        Add("XREAD", -4, false, static (c, a) => StreamRead(c, a),
            "Entries newer than an id, from one or more streams: XREAD [COUNT n] [BLOCK ms] STREAMS key... id...");
        Add("XGROUP", -2, true, static (c, a) => StreamGroupCommand(c, a),
            "Manages consumer groups: CREATE, DESTROY, CREATECONSUMER, DELCONSUMER, SETID.");
        Add("XREADGROUP", -7, true, static (c, a) => StreamReadGroup(c, a),
            "Reads as a group member: XREADGROUP GROUP g c [COUNT n] [BLOCK ms] [NOACK] STREAMS key... id...");
        Add("XACK", -4, true, static (c, a) => RespValue.Number64(
            c.Db.StreamAck(a[1], a[2], a[3..].Select(id => ParseStreamId(id, 0)).ToArray())),
            "Acknowledges entries, removing them from the pending list.");
        Add("XPENDING", -3, false, static (c, a) => StreamPending(c, a),
            "Pending entries: a summary, or XPENDING key group [IDLE ms] start end count [consumer].");
        Add("XCLAIM", -6, true, static (c, a) => StreamClaim(c, a),
            "Takes over idle pending entries: XCLAIM key group consumer min-idle id... [options].");
        Add("XAUTOCLAIM", -6, true, static (c, a) => StreamAutoClaim(c, a),
            "Scans and claims idle pending entries: XAUTOCLAIM key group consumer min-idle start [COUNT n] [JUSTID].");
        Add("XINFO", -3, false, static (c, a) => StreamInfo(c, a),
            "Describes a stream: XINFO STREAM key | GROUPS key | CONSUMERS key group.");

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
            // An entry with no fields is a pending id whose entry has been trimmed - Redis's nil.
            items[i] = RespValue.Array(
                RespValue.Bulk(entries[i].Id.ToString()),
                entries[i].Fields.Length == 0 ? RespValue.Null : RespValue.BulkArray(entries[i].Fields.Select(f => (string?)f)));
        }
        return RespValue.Array(items);
    }

    /// <summary>The Redis version MemSharp reports for clients that pick features by version.</summary>
    /// <remarks>
    /// Several client libraries read <c>redis_version</c> from <c>INFO</c> to decide which commands
    /// they may send. 6.2 is the release whose command surface MemSharp's most closely matches - it
    /// predates RESP3-by-default and the 7.x command additions - so a client that trusts it will not
    /// reach for commands MemSharp lacks. It is a compatibility level, not a claim to be Redis.
    /// </remarks>
    internal const string RedisCompatibilityVersion = "6.2.0";

    private static readonly string[] DefaultInfoSections =
        ["server", "clients", "memory", "persistence", "stats", "replication", "cpu", "keyspace"];

    /// <summary>
    /// The <c>INFO</c> text: Redis's section names and field names, so existing dashboards and
    /// exporters can read it, plus MemSharp's own fields under the same sections.
    /// </summary>
    /// <remarks>
    /// The 1.0 field names (<c>product</c>, <c>keys</c>, <c>uptime_seconds</c> and the rest) are all
    /// still present: the published 1.0 clients parse them.
    /// </remarks>
    private static string BuildInfo(MemDb db, string[] requested)
    {
        var sections = requested.Length == 0 ||
                       requested.Any(s => s.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                          s.Equals("everything", StringComparison.OrdinalIgnoreCase) ||
                                          s.Equals("default", StringComparison.OrdinalIgnoreCase))
            ? DefaultInfoSections
            : requested.Select(s => s.ToLowerInvariant()).ToArray();

        var stats = db.Statistics.Snapshot();
        var text = new StringBuilder();

        void Section(string name, params (string Field, object Value)[] fields)
        {
            if (!sections.Contains(name)) return;
            if (text.Length > 0) text.Append("\r\n");
            text.Append("# ").Append(char.ToUpperInvariant(name[0])).Append(name, 1, name.Length - 1).Append("\r\n");
            foreach (var (field, value) in fields)
            {
                text.Append(field).Append(':').Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("\r\n");
            }
        }

        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        long used = GC.GetTotalMemory(forceFullCollection: false);
        long uptime = (long)stats.Uptime.TotalSeconds;
        string version = typeof(MemDb).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        Section("server",
            ("redis_version", RedisCompatibilityVersion),
            ("memsharp_version", version),
            ("redis_mode", "standalone"),
            ("os", RuntimeInformation.OSDescription),
            ("arch_bits", IntPtr.Size * 8),
            ("runtime", RuntimeInformation.FrameworkDescription),
            ("process_id", Environment.ProcessId),
            ("uptime_in_seconds", uptime),
            ("uptime_in_days", uptime / 86_400),
            ("product", "MemSharp"),
            ("vendor", "Gravicode Studios"),
            ("version", version),
            ("uptime_seconds", uptime));
        Section("clients",
            ("connected_clients", stats.ConnectedClients),
            ("blocked_clients", stats.BlockedClients),
            ("connections_accepted", stats.ConnectionsAccepted));
        Section("memory",
            ("used_memory", used),
            ("used_memory_human", HumanBytes(used)),
            ("used_memory_rss", process.WorkingSet64),
            ("used_memory_rss_human", HumanBytes(process.WorkingSet64)),
            ("used_memory_peak", process.PeakWorkingSet64),
            ("used_memory_peak_human", HumanBytes(process.PeakWorkingSet64)),
            ("total_system_memory", gc.TotalAvailableMemoryBytes),
            ("total_system_memory_human", HumanBytes(gc.TotalAvailableMemoryBytes)),
            ("maxmemory", 0),
            ("maxmemory_policy", "noeviction"),
            ("mem_allocator", GCSettings.IsServerGC ? "dotnet-server-gc" : "dotnet-workstation-gc"),
            ("gc_collections_gen0", GC.CollectionCount(0)),
            ("gc_collections_gen1", GC.CollectionCount(1)),
            ("gc_collections_gen2", GC.CollectionCount(2)));
        Section("persistence",
            ("loading", 0),
            ("rdb_changes_since_last_save", db.PendingChanges),
            ("rdb_last_save_time", db.LastSaveTime?.ToUnixTimeSeconds() ?? 0),
            ("aof_enabled", db.Options.Persistence.AppendOnly is null ? 0 : 1),
            ("last_save", db.LastSaveTime?.ToUnixTimeSeconds() ?? 0),
            ("pending_changes", db.PendingChanges));
        Section("stats",
            ("total_connections_received", stats.ConnectionsAccepted),
            ("total_commands_processed", stats.CommandsProcessed),
            ("keyspace_hits", stats.Hits),
            ("keyspace_misses", stats.Misses),
            ("expired_keys", stats.ExpiredKeys),
            ("evicted_keys", 0),
            ("total_writes", stats.Writes),
            ("pubsub_messages_published", stats.Publishes),
            ("commands_processed", stats.CommandsProcessed),
            ("hit_rate", stats.HitRate.ToString("F4", CultureInfo.InvariantCulture)),
            ("writes", stats.Writes),
            ("pubsub_messages", stats.MessagesDelivered));
        Section("replication",
            ("role", "master"),
            ("connected_slaves", 0));
        Section("cpu",
            ("used_cpu_sys", process.PrivilegedProcessorTime.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture)),
            ("used_cpu_user", process.UserProcessorTime.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture)));

        long keys = db.Count;
        var keyspace = new List<(string, object)> { ("keys", keys), ("shards", db.ShardCount) };
        if (keys > 0) keyspace.Insert(0, ("db0", $"keys={keys},expires={db.VolatileKeyCount},avg_ttl=0"));
        Section("keyspace", keyspace.ToArray());

        return text.ToString();
    }

    private static string HumanBytes(long bytes)
    {
        string[] units = ["B", "K", "M", "G", "T"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes}B" : value.ToString("0.00", CultureInfo.InvariantCulture) + units[unit];
    }

    /// <summary>
    /// <c>CONFIG GET</c>: the parameters monitoring tools ask for, answered from the options the
    /// database was built with. Patterns are globs, as in Redis.
    /// </summary>
    private static RespValue ConfigGet(MemDb db, string[] patterns)
    {
        var options = db.Options;
        var persistence = options.Persistence;
        string? snapshot = persistence.SnapshotPath;

        var parameters = new (string Name, string Value)[]
        {
            ("databases", "1"),
            ("maxmemory", "0"),
            ("maxmemory-policy", "noeviction"),
            ("timeout", "0"),
            ("save", persistence.Mode == PersistenceMode.Automatic
                ? $"{(long)persistence.AutoSaveInterval.TotalSeconds} {persistence.AutoSaveAfterChanges}"
                : ""),
            ("dir", snapshot is null ? "" : Path.GetDirectoryName(Path.GetFullPath(snapshot)) ?? ""),
            ("dbfilename", snapshot is null ? "" : Path.GetFileName(snapshot)),
            ("appendonly", persistence.AppendOnly is null ? "no" : "yes"),
            ("appendfilename", persistence.AppendOnly?.Path ?? ""),
            ("appendfsync", persistence.AppendOnly?.Fsync switch
            {
                FsyncPolicy.Always => "always",
                FsyncPolicy.Never => "no",
                _ => "everysec",
            }),
            ("shards", db.ShardCount.ToString(CultureInfo.InvariantCulture)),
            ("expiry-sweep-interval-ms", ((long)options.ExpirySweepInterval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
        };

        var items = new List<RespValue>();
        foreach (var (name, value) in parameters)
        {
            if (!patterns.Any(p => GlobMatcher.IsMatch(p.ToLowerInvariant(), name))) continue;
            items.Add(RespValue.Bulk(name));
            items.Add(RespValue.Bulk(value));
        }
        return RespValue.Array(items.ToArray());
    }

    private static RespValue ResetStatistics(MemDb db)
    {
        db.Statistics.Reset();
        return RespValue.Ok;
    }

    private static RespValue SortedStore(CommandContext c, string[] a, bool intersect)
    {
        int keyCount = ParseInt(a[2]);
        if (keyCount <= 0) return RespValue.Error("ERR", "at least 1 input key is needed");
        if (3 + keyCount > a.Length) return RespValue.Error("ERR", "syntax error");

        var keys = a[3..(3 + keyCount)];
        double[]? weights = null;
        var aggregate = ScoreAggregate.Sum;

        for (int i = 3 + keyCount; i < a.Length; i++)
        {
            if (a[i].Equals("WEIGHTS", StringComparison.OrdinalIgnoreCase) && i + keyCount < a.Length)
            {
                weights = new double[keyCount];
                for (int w = 0; w < keyCount; w++) weights[w] = ParseDouble(a[++i]);
            }
            else if (a[i].Equals("AGGREGATE", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Length)
            {
                aggregate = a[++i].ToUpperInvariant() switch
                {
                    "SUM" => ScoreAggregate.Sum,
                    "MIN" => ScoreAggregate.Min,
                    "MAX" => ScoreAggregate.Max,
                    _ => throw new MemSharpCommandException("syntax error"),
                };
            }
            else
            {
                return RespValue.Error("ERR", "syntax error");
            }
        }

        int stored = intersect
            ? c.Db.SortedSetIntersectStore(a[1], keys, weights, aggregate)
            : c.Db.SortedSetUnionStore(a[1], keys, weights, aggregate);
        return RespValue.Number64(stored);
    }

    // ---- stream reads and consumer groups -----------------------------------------------------------

    /// <summary>
    /// The BLOCK timeout of an <c>XREAD</c> or <c>XREADGROUP</c>, if it has one. Zero means "forever".
    /// </summary>
    /// <remarks>
    /// The handlers themselves never block - the table is synchronous and is also what replays the
    /// append-only log. A server connection asks this first, and when a blocking read comes back
    /// empty it waits for the next append and runs the command again. Embedded callers and replay
    /// see the non-blocking answer.
    /// </remarks>
    public static bool TryGetBlockTimeout(string[] arguments, out TimeSpan timeout)
    {
        timeout = default;
        if (arguments.Length == 0) return false;
        string name = arguments[0].ToUpperInvariant();
        if (name is not ("XREAD" or "XREADGROUP")) return false;

        for (int i = 1; i + 1 < arguments.Length; i++)
        {
            if (arguments[i].Equals("STREAMS", StringComparison.OrdinalIgnoreCase)) return false;
            if (arguments[i].Equals("BLOCK", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(arguments[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms) || ms < 0)
                {
                    return false;   // the handler reports the malformed value
                }
                timeout = TimeSpan.FromMilliseconds(ms);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Pins <c>$</c> in a blocking <c>XREAD</c> to the id it means right now. Retried after a wake-up,
    /// a literal <c>$</c> would be re-read as the <em>new</em> last id, and the client would sleep
    /// through exactly the entry that woke it.
    /// </summary>
    public static string[] ResolveForBlocking(MemDb db, string[] arguments)
    {
        if (!arguments[0].Equals("XREAD", StringComparison.OrdinalIgnoreCase)) return arguments;
        int streams = Array.FindIndex(arguments, a => a.Equals("STREAMS", StringComparison.OrdinalIgnoreCase));
        if (streams < 0) return arguments;

        int pairs = (arguments.Length - streams - 1) / 2;
        var resolved = (string[])arguments.Clone();
        for (int i = 0; i < pairs; i++)
        {
            int idIndex = streams + 1 + pairs + i;
            if (resolved[idIndex] != "$") continue;
            resolved[idIndex] = (db.StreamLastId(resolved[streams + 1 + i]) ?? StreamId.Min).ToString();
        }
        return resolved;
    }

    /// <summary>Splits <c>STREAMS k1 k2 id1 id2</c> into its keys and ids.</summary>
    private static (string[] Keys, string[] Ids) ParseStreamsClause(string[] a, int start)
    {
        int streams = -1;
        for (int i = start; i < a.Length; i++)
        {
            if (a[i].Equals("STREAMS", StringComparison.OrdinalIgnoreCase)) { streams = i; break; }
        }
        int rest = streams < 0 ? 0 : a.Length - streams - 1;
        if (streams < 0 || rest == 0 || rest % 2 != 0)
        {
            throw new MemSharpCommandException(
                "Unbalanced STREAMS list: each stream key needs exactly one id, after the keys");
        }
        int half = rest / 2;
        return (a[(streams + 1)..(streams + 1 + half)], a[(streams + 1 + half)..]);
    }

    private static RespValue StreamRead(CommandContext c, string[] a)
    {
        int count = 0;
        for (int i = 1; i + 1 < a.Length && !a[i].Equals("STREAMS", StringComparison.OrdinalIgnoreCase); i++)
        {
            if (a[i].Equals("COUNT", StringComparison.OrdinalIgnoreCase)) count = ParseInt(a[++i]);
            else if (a[i].Equals("BLOCK", StringComparison.OrdinalIgnoreCase)) ParseLong(a[++i]);
        }

        var (keys, ids) = ParseStreamsClause(a, 1);
        var replies = new List<RespValue>();
        for (int i = 0; i < keys.Length; i++)
        {
            var after = ids[i] == "$"
                ? c.Db.StreamLastId(keys[i]) ?? StreamId.Min
                : ParseStreamId(ids[i], 0);
            var entries = c.Db.StreamReadAfter(keys[i], after, count <= 0 ? -1 : count);
            if (entries.Count > 0) replies.Add(RespValue.Array(RespValue.Bulk(keys[i]), RenderStream(entries)));
        }

        // Nil, not an empty array, when nothing is new: the shape a blocking client waits on.
        return replies.Count == 0 ? RespValue.Null : RespValue.Array(replies.ToArray());
    }

    private static RespValue StreamReadGroup(CommandContext c, string[] a)
    {
        if (!a[1].Equals("GROUP", StringComparison.OrdinalIgnoreCase)) return RespValue.Error("ERR", "syntax error: expected GROUP");
        string group = a[2], consumer = a[3];
        int count = 0;
        bool noAck = false;

        for (int i = 4; i < a.Length && !a[i].Equals("STREAMS", StringComparison.OrdinalIgnoreCase); i++)
        {
            switch (a[i].ToUpperInvariant())
            {
                case "COUNT" when i + 1 < a.Length: count = ParseInt(a[++i]); break;
                case "BLOCK" when i + 1 < a.Length: ParseLong(a[++i]); break;
                case "NOACK": noAck = true; break;
                default: return RespValue.Error("ERR", "syntax error");
            }
        }

        var (keys, ids) = ParseStreamsClause(a, 4);
        var replies = new List<RespValue>();
        for (int i = 0; i < keys.Length; i++)
        {
            bool fresh = ids[i] == ">";
            var entries = c.Db.StreamReadGroup(keys[i], group, consumer, fresh ? null : ParseStreamId(ids[i], 0), count, noAck);

            // A history read always reports its stream, even when empty: that empty list is how a
            // recovering worker learns it has nothing left to redo.
            if (entries.Count > 0 || !fresh) replies.Add(RespValue.Array(RespValue.Bulk(keys[i]), RenderStream(entries)));
        }
        return replies.Count == 0 ? RespValue.Null : RespValue.Array(replies.ToArray());
    }

    private static RespValue StreamGroupCommand(CommandContext c, string[] a)
    {
        string sub = a[1].ToUpperInvariant();
        RespValue Usage() => RespValue.Error("ERR", $"wrong number of arguments for 'XGROUP {sub}'");

        switch (sub)
        {
            case "CREATE":
            {
                if (a.Length < 5) return Usage();
                bool makeStream = false;
                for (int i = 5; i < a.Length; i++)
                {
                    if (a[i].Equals("MKSTREAM", StringComparison.OrdinalIgnoreCase)) makeStream = true;
                    else if (a[i].Equals("ENTRIESREAD", StringComparison.OrdinalIgnoreCase)) i++;   // lag tracking is not implemented
                    else return RespValue.Error("ERR", "syntax error");
                }
                c.Db.StreamGroupCreate(a[2], a[3], a[4] == "$" ? null : ParseStreamId(a[4], 0), makeStream);
                return RespValue.Ok;
            }
            case "DESTROY":
                return a.Length == 4 ? RespValue.Boolean(c.Db.StreamGroupDestroy(a[2], a[3])) : Usage();
            case "CREATECONSUMER":
                return a.Length == 5 ? RespValue.Boolean(c.Db.StreamGroupCreateConsumer(a[2], a[3], a[4])) : Usage();
            case "DELCONSUMER":
                return a.Length == 5 ? RespValue.Number64(c.Db.StreamGroupDeleteConsumer(a[2], a[3], a[4])) : Usage();
            case "SETID":
                if (a.Length < 5) return Usage();
                c.Db.StreamGroupSetId(a[2], a[3], a[4] == "$" ? null : ParseStreamId(a[4], 0));
                return RespValue.Ok;
            default:
                return RespValue.Error("ERR", $"unknown XGROUP subcommand '{a[1]}'");
        }
    }

    private static RespValue StreamPending(CommandContext c, string[] a)
    {
        if (a.Length == 3)
        {
            var summary = c.Db.StreamPending(a[1], a[2]);
            if (summary.Count == 0) return RespValue.Array(RespValue.Number64(0), RespValue.Null, RespValue.Null, RespValue.Null);
            return RespValue.Array(
                RespValue.Number64(summary.Count),
                RespValue.Bulk(summary.Lowest!.Value.ToString()),
                RespValue.Bulk(summary.Highest!.Value.ToString()),
                RespValue.Array(summary.Consumers.Select(p => RespValue.Array(
                    RespValue.Bulk(p.Key), RespValue.Bulk(p.Value.ToString(CultureInfo.InvariantCulture)))).ToArray()));
        }

        int i = 3;
        var minIdle = TimeSpan.Zero;
        if (a[i].Equals("IDLE", StringComparison.OrdinalIgnoreCase))
        {
            if (a.Length < 5) return RespValue.Error("ERR", "syntax error");
            minIdle = TimeSpan.FromMilliseconds(ParseLong(a[i + 1]));
            i += 2;
        }
        if (a.Length - i is not (3 or 4)) return RespValue.Error("ERR", "syntax error");

        var entries = c.Db.StreamPendingRange(
            a[1], a[2], ParseStreamBound(a[i], StreamId.Min), ParseStreamBound(a[i + 1], StreamId.Max),
            ParseInt(a[i + 2]), a.Length - i == 4 ? a[i + 3] : null, minIdle);
        return RespValue.Array(entries.Select(e => RespValue.Array(
            RespValue.Bulk(e.Id.ToString()),
            RespValue.Bulk(e.Consumer),
            RespValue.Number64((long)e.Idle.TotalMilliseconds),
            RespValue.Number64(e.DeliveryCount))).ToArray());
    }

    private static RespValue StreamClaim(CommandContext c, string[] a)
    {
        var minIdle = TimeSpan.FromMilliseconds(ParseLong(a[4]));
        var ids = new List<StreamId>();
        var options = new StreamClaimOptions();

        int i = 5;
        for (; i < a.Length && StreamId.TryParse(a[i], 0, out var id); i++) ids.Add(id);
        for (; i < a.Length; i++)
        {
            switch (a[i].ToUpperInvariant())
            {
                case "IDLE" when i + 1 < a.Length: options.Idle = TimeSpan.FromMilliseconds(ParseLong(a[++i])); break;
                case "TIME" when i + 1 < a.Length: options.TimeMilliseconds = ParseLong(a[++i]); break;
                case "RETRYCOUNT" when i + 1 < a.Length: options.RetryCount = ParseLong(a[++i]); break;
                case "LASTID" when i + 1 < a.Length: options.LastId = ParseStreamId(a[++i], 0); break;
                case "FORCE": options.Force = true; break;
                case "JUSTID": options.JustId = true; break;
                default: return RespValue.Error("ERR", $"unrecognized XCLAIM option '{a[i]}'");
            }
        }
        if (ids.Count == 0) return RespValue.Error("ERR", "XCLAIM needs at least one id");

        var claimed = c.Db.StreamClaim(a[1], a[2], a[3], minIdle, ids.ToArray(), options);
        return options.JustId
            ? RespValue.BulkArray(claimed.Select(e => (string?)e.Id.ToString()))
            : RenderStream(claimed);
    }

    private static RespValue StreamAutoClaim(CommandContext c, string[] a)
    {
        int count = 100;
        bool justId = false;
        for (int i = 6; i < a.Length; i++)
        {
            if (a[i].Equals("COUNT", StringComparison.OrdinalIgnoreCase) && i + 1 < a.Length) count = ParseInt(a[++i]);
            else if (a[i].Equals("JUSTID", StringComparison.OrdinalIgnoreCase)) justId = true;
            else return RespValue.Error("ERR", "syntax error");
        }

        var result = c.Db.StreamAutoClaim(
            a[1], a[2], a[3], TimeSpan.FromMilliseconds(ParseLong(a[4])), ParseStreamBound(a[5], StreamId.Min), count, justId);
        return RespValue.Array(
            RespValue.Bulk(result.NextStart.ToString()),
            justId ? RespValue.BulkArray(result.Claimed.Select(e => (string?)e.Id.ToString())) : RenderStream(result.Claimed),
            RespValue.BulkArray(result.Deleted.Select(d => (string?)d.ToString())));
    }

    private static RespValue StreamInfo(CommandContext c, string[] a)
    {
        switch (a[1].ToUpperInvariant())
        {
            case "GROUPS":
                return RespValue.Array(c.Db.StreamGroups(a[2]).Select(g => RespValue.Array(
                    RespValue.Bulk("name"), RespValue.Bulk(g.Name),
                    RespValue.Bulk("consumers"), RespValue.Number64(g.Consumers),
                    RespValue.Bulk("pending"), RespValue.Number64(g.Pending),
                    RespValue.Bulk("last-delivered-id"), RespValue.Bulk(g.LastDeliveredId.ToString()))).ToArray());

            case "CONSUMERS" when a.Length >= 4:
                return RespValue.Array(c.Db.StreamConsumers(a[2], a[3]).Select(x => RespValue.Array(
                    RespValue.Bulk("name"), RespValue.Bulk(x.Name),
                    RespValue.Bulk("pending"), RespValue.Number64(x.Pending),
                    RespValue.Bulk("idle"), RespValue.Number64((long)x.Idle.TotalMilliseconds))).ToArray());

            case "STREAM":
            {
                if (c.Db.TypeOf(a[2]) != MemType.Stream) return RespValue.Error("ERR", "no such key");
                var first = c.Db.StreamRange(a[2], limit: 1);
                var last = c.Db.StreamRange(a[2], descending: true, limit: 1);
                return RespValue.Array(
                    RespValue.Bulk("length"), RespValue.Number64(c.Db.StreamLength(a[2])),
                    RespValue.Bulk("last-generated-id"), RespValue.Bulk((c.Db.StreamLastId(a[2]) ?? StreamId.Min).ToString()),
                    RespValue.Bulk("groups"), RespValue.Number64(c.Db.StreamGroups(a[2]).Count),
                    RespValue.Bulk("first-entry"), first.Count > 0 ? RenderStream(first).Items![0] : RespValue.Null,
                    RespValue.Bulk("last-entry"), last.Count > 0 ? RenderStream(last).Items![0] : RespValue.Null);
            }

            default:
                return RespValue.Error("ERR", $"unknown XINFO subcommand '{a[1]}', or wrong number of arguments");
        }
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
