using System.Buffers;
using System.Text;
using MemSharp.Client;
using MemSharp.Commands;
using MemSharp.Protocol;
using MemSharp.Server;
using Xunit;

namespace MemSharp.Tests;

public class RespReaderTests
{
    private static ReadOnlySequence<byte> Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ParsesACommandArray()
    {
        Assert.True(RespReader.TryParseCommand(Bytes("*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$1\r\nv\r\n"), out var command, out long consumed));
        Assert.Equal(["SET", "k", "v"], command);

        // 4 bytes for the array header, then 9 + 7 + 7 for the three bulk strings.
        Assert.Equal(27, consumed);
    }

    [Fact]
    public void ParsesAnInlineCommand()
    {
        Assert.True(RespReader.TryParseCommand(Bytes("SET k v\r\n"), out var command, out _));
        Assert.Equal(["SET", "k", "v"], command);
    }

    [Fact]
    public void InlineCommandHonoursQuotes()
    {
        Assert.True(RespReader.TryParseCommand(Bytes("SET greeting \"hello world\"\r\n"), out var command, out _));
        Assert.Equal(["SET", "greeting", "hello world"], command);
    }

    [Theory]
    [InlineData("*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$1\r\n")]     // payload missing
    [InlineData("*3\r\n$3\r\nSET\r\n")]                       // arguments missing
    [InlineData("*3\r\n")]                                    // header only
    [InlineData("SET k v")]                                   // inline with no terminator
    [InlineData("")]                                          // nothing at all
    public void IncompleteInputIsNotConsumed(string partial)
    {
        // Returning false rather than throwing is what makes the server correct under TCP
        // segmentation: the bytes stay in the pipe until the rest arrives.
        Assert.False(RespReader.TryParseCommand(Bytes(partial), out _, out _));
    }

    [Fact]
    public void TwoPipelinedCommandsAreParsedInSequence()
    {
        var buffer = Bytes("*1\r\n$4\r\nPING\r\n*1\r\n$4\r\nPING\r\n");

        Assert.True(RespReader.TryParseCommand(buffer, out var first, out long consumed));
        Assert.Equal(["PING"], first);

        Assert.True(RespReader.TryParseCommand(buffer.Slice(consumed), out var second, out _));
        Assert.Equal(["PING"], second);
    }

    [Fact]
    public void CommandSplitAcrossSegmentsStillParses()
    {
        // Two segments with a value straddling the boundary - what a real socket delivers.
        var first = new BufferSegment("*3\r\n$3\r\nSET\r\n$5\r\nhe"u8.ToArray());
        var second = first.Append("llo\r\n$5\r\nworld\r\n"u8.ToArray());
        var sequence = new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);

        Assert.True(RespReader.TryParseCommand(sequence, out var command, out _));
        Assert.Equal(["SET", "hello", "world"], command);
    }

    [Fact]
    public void MalformedLengthPrefixThrows()
    {
        Assert.Throws<MemSharpCommandException>(() =>
            RespReader.TryParseCommand(Bytes("*abc\r\n"), out _, out _));
    }

    [Fact]
    public void AbsurdArgumentCountIsRejected()
    {
        // A hostile length prefix must not become a multi-gigabyte allocation.
        Assert.Throws<MemSharpCommandException>(() =>
            RespReader.TryParseCommand(Bytes("*99999999\r\n"), out _, out _));
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(byte[] data) => Memory = data;

        public BufferSegment Append(byte[] data)
        {
            var next = new BufferSegment(data) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}

public class RespWriterTests
{
    private static string Write(RespValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        RespWriter.Write(buffer, value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public void WritesEachWireType()
    {
        Assert.Equal("+OK\r\n", Write(RespValue.Ok));
        Assert.Equal(":42\r\n", Write(RespValue.Number64(42)));
        Assert.Equal(":-7\r\n", Write(RespValue.Number64(-7)));
        Assert.Equal("$5\r\nhello\r\n", Write(RespValue.Bulk("hello")));
        Assert.Equal("$-1\r\n", Write(RespValue.Null));
        Assert.Equal("-ERR broke\r\n", Write(RespValue.Error("ERR", "broke")));
        Assert.Equal("*0\r\n", Write(RespValue.EmptyArray));
    }

    [Fact]
    public void BulkLengthIsInBytesNotCharacters()
    {
        // A multi-byte character counted as one would under-declare the length and desynchronise
        // every subsequent reply on the connection.
        Assert.Equal("$7\r\nhalo é\r\n", Write(RespValue.Bulk("halo é")));
    }

    [Fact]
    public void NestedArraysRoundTripThroughTheReader()
    {
        var original = RespValue.Array(
            RespValue.Bulk("outer"),
            RespValue.Array(RespValue.Number64(1), RespValue.Bulk("inner")));

        var buffer = new ArrayBufferWriter<byte>();
        RespWriter.Write(buffer, original);

        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        var reader = new SequenceReader<byte>(sequence);
        Assert.True(RespReader.TryParseValue(ref reader, out var parsed));

        Assert.Equal(2, parsed!.Items!.Length);
        Assert.Equal("outer", parsed.Items[0].Text);
        Assert.Equal(1, parsed.Items[1].Items![0].Integer);
    }

    [Fact]
    public void CommandsAreWrittenAsBulkArrays()
    {
        var buffer = new ArrayBufferWriter<byte>();
        RespWriter.WriteCommand(buffer, "SET", "k", "v");
        Assert.Equal("*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$1\r\nv\r\n", Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}

public class CommandTableTests
{
    private static RespValue Run(MemDb db, params string[] arguments) =>
        CommandTable.Execute(new MemSharp.Commands.CommandContext(db, null), arguments);

    [Fact]
    public void UnknownCommandIsAnError()
    {
        using var db = TestDb.Create();
        var reply = Run(db, "NOSUCHTHING");
        Assert.Equal(RespKind.Error, reply.Kind);
    }

    [Theory]
    [InlineData("GET")]                       // too few
    [InlineData("GET", "a", "b")]             // too many
    [InlineData("SET", "onlykey")]
    public void ArityIsEnforced(params string[] arguments)
    {
        using var db = TestDb.Create();
        var reply = Run(db, arguments);
        Assert.Equal(RespKind.Error, reply.Kind);
        Assert.Contains("wrong number of arguments", reply.Text);
    }

    [Fact]
    public void EngineExceptionsBecomeErrorReplies()
    {
        using var db = TestDb.Create();
        db.ListPushRight("l", "x");

        var reply = Run(db, "GET", "l");

        // The exception must not escape into the connection loop and kill the connection.
        Assert.Equal(RespKind.Error, reply.Kind);
        Assert.StartsWith("WRONGTYPE", reply.Text);
    }

    [Fact]
    public void SetHonoursExpiryAndNxOptions()
    {
        using var db = TestDb.Create();

        Assert.Equal("OK", Run(db, "SET", "k", "v", "EX", "60").Text);
        Assert.NotNull(db.TimeToLive("k"));

        Assert.True(Run(db, "SET", "k", "other", "NX").IsNull);
        Assert.Equal("v", db.Get("k"));
    }

    [Fact]
    public void TtlUsesRedisSentinelValues()
    {
        using var db = TestDb.Create();
        db.Set("permanent", "v");
        db.Set("volatile", "v", TimeSpan.FromMinutes(5));

        Assert.Equal(-2, Run(db, "TTL", "absent").Integer);     // no such key
        Assert.Equal(-1, Run(db, "TTL", "permanent").Integer);  // exists, never expires
        Assert.InRange(Run(db, "TTL", "volatile").Integer, 1, 300);
    }

    [Fact]
    public void ZAddParsesScoreMemberPairs()
    {
        using var db = TestDb.Create();
        Assert.Equal(2, Run(db, "ZADD", "z", "1.5", "a", "2.5", "b").Integer);
        Assert.Equal(1.5, db.SortedSetScore("z", "a"));
        Assert.Equal(2.5, db.SortedSetScore("z", "b"));
    }

    [Fact]
    public void ZRangeWithScoresInterleavesMembersAndScores()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z", "a", 1);
        db.SortedSetAdd("z", "b", 2);

        var reply = Run(db, "ZRANGE", "z", "0", "-1", "WITHSCORES");

        Assert.Equal(["a", "1", "b", "2"], reply.Items!.Select(i => i.Text));
    }

    [Fact]
    public void ScoreBoundsAcceptInfinity()
    {
        using var db = TestDb.Create();
        db.SortedSetAdd("z", "a", -1000);
        db.SortedSetAdd("z", "b", 1000);

        Assert.Equal(2, Run(db, "ZCOUNT", "z", "-inf", "+inf").Integer);
    }

    [Fact]
    public void ScanReturnsACursorAndAPage()
    {
        using var db = TestDb.Create();
        for (int i = 0; i < 250; i++) db.Set($"k{i}", "v");

        var reply = Run(db, "SCAN", "0", "COUNT", "100");

        Assert.Equal(2, reply.Items!.Length);
        Assert.Equal("100", reply.Items[0].Text);           // more to come
        Assert.Equal(100, reply.Items[1].Items!.Length);
    }

    [Fact]
    public void ScanCursorReachesZeroAtTheEnd()
    {
        using var db = TestDb.Create();
        for (int i = 0; i < 10; i++) db.Set($"k{i}", "v");

        var reply = Run(db, "SCAN", "0", "COUNT", "100");
        Assert.Equal("0", reply.Items![0].Text);
    }

    [Fact]
    public void EveryCommandHasASummary()
    {
        Assert.All(CommandTable.All, command =>
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Summary));
            Assert.Equal(command.Name.ToUpperInvariant(), command.Name);
        });
    }

    [Fact]
    public void InfoIncludesTheAttribution()
    {
        using var db = TestDb.Create();
        var info = Run(db, "INFO").Text!;
        Assert.Contains("Gravicode Studios", info);
        Assert.Contains("MemSharp", info);
    }
}

public class ServerTests
{
    private static async Task<(MemDb Db, MemServer Server, int Port)> StartAsync()
    {
        var db = TestDb.Create();
        var server = new MemServer(db, new MemServerOptions { Port = 0 });
        await server.StartAsync();
        return (db, server, server.EndPoint!.Port);
    }

    [Fact]
    public async Task RoundTripsACommand()
    {
        var (db, server, port) = await StartAsync();
        await using var runningServer = server;
        using var openDb = db;

        await using var client = new MemClient();
        await client.ConnectAsync("127.0.0.1", port);

        Assert.Equal("PONG", (await client.ExecuteAsync("PING")).Text);
        await client.ExecuteAsync("SET", "k", "v");
        Assert.Equal("v", (await client.ExecuteAsync("GET", "k")).Text);
        Assert.Equal("v", db.Get("k"));           // the server and its host share one database
    }

    [Fact]
    public async Task PipelinedBatchReturnsEveryReplyInOrder()
    {
        var (db, server, port) = await StartAsync();
        await using var runningServer = server;
        using var openDb = db;

        await using var client = new MemClient();
        await client.ConnectAsync("127.0.0.1", port);

        var batch = Enumerable.Range(0, 500).Select(i => new[] { "SET", $"k{i}", i.ToString() }).ToList();
        var replies = await client.PipelineAsync(batch);

        Assert.Equal(500, replies.Length);
        Assert.All(replies, r => Assert.Equal("OK", r.Text));

        var reads = Enumerable.Range(0, 500).Select(i => new[] { "GET", $"k{i}" }).ToList();
        var values = await client.PipelineAsync(reads);
        Assert.Equal(Enumerable.Range(0, 500).Select(i => i.ToString()), values.Select(v => v.Text));
    }

    [Fact]
    public async Task ErrorsCrossTheWireWithTheirCode()
    {
        var (db, server, port) = await StartAsync();
        await using var runningServer = server;
        using var openDb = db;

        await using var client = new MemClient();
        await client.ConnectAsync("127.0.0.1", port);

        await client.ExecuteAsync("SET", "k", "v");
        var reply = await client.ExecuteAsync("LPUSH", "k", "x");

        Assert.Equal(RespKind.Error, reply.Kind);
        Assert.StartsWith("WRONGTYPE", reply.Text);
    }

    [Fact]
    public async Task ConnectionSurvivesAnErrorReply()
    {
        var (db, server, port) = await StartAsync();
        await using var runningServer = server;
        using var openDb = db;

        await using var client = new MemClient();
        await client.ConnectAsync("127.0.0.1", port);

        await client.ExecuteAsync("NOSUCHCOMMAND");
        Assert.Equal("PONG", (await client.ExecuteAsync("PING")).Text);
    }

    [Fact]
    public async Task PubSubDeliversToASubscribedConnection()
    {
        var (db, server, port) = await StartAsync();
        await using var runningServer = server;
        using var openDb = db;

        await using var subscriber = new MemClient();
        await subscriber.ConnectAsync("127.0.0.1", port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = new List<string>();
        var pump = Task.Run(async () =>
        {
            await foreach (var message in subscriber.SubscribeAsync("chan", cts.Token))
            {
                received.Add(message.Message);
                if (received.Count == 3) return;
            }
        }, cts.Token);

        await using var publisher = new MemClient();
        await publisher.ConnectAsync("127.0.0.1", port);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (db.SubscriberCount("chan") == 0 && DateTime.UtcNow < deadline) await Task.Delay(20, cts.Token);

        for (int i = 1; i <= 3; i++) await publisher.ExecuteAsync("PUBLISH", "chan", $"m{i}");
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["m1", "m2", "m3"], received);
    }

    [Fact]
    public async Task DisconnectingUnregistersTheSubscription()
    {
        var (db, server, port) = await StartAsync();
        await using var runningServer = server;
        using var openDb = db;

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await using var subscriber = new MemClient();
            await subscriber.ConnectAsync("127.0.0.1", port);
            var pump = Task.Run(async () =>
            {
                await foreach (var unused in subscriber.SubscribeAsync("chan", cts.Token)) { }
            }, cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (db.SubscriberCount("chan") == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(1, db.SubscriberCount("chan"));
        }

        // A subscriber that never unregisters would leave a dead callback receiving every publish
        // for the life of the process - the leak the original engine had.
        var cleanup = DateTime.UtcNow.AddSeconds(10);
        while (db.SubscriberCount("chan") > 0 && DateTime.UtcNow < cleanup) await Task.Delay(50);

        Assert.Equal(0, db.SubscriberCount("chan"));
    }

    [Fact]
    public async Task ServerStopsCleanly()
    {
        var (db, server, port) = await StartAsync();
        using var openDb = db;

        await using (var client = new MemClient())
        {
            await client.ConnectAsync("127.0.0.1", port);
            await client.ExecuteAsync("PING");
        }

        await server.StopAsync();
        await server.DisposeAsync();

        await using var refused = new MemClient();
        await Assert.ThrowsAnyAsync<Exception>(() => refused.ConnectAsync("127.0.0.1", port));
    }
}
