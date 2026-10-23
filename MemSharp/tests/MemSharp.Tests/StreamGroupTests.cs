using MemSharp.Client;
using MemSharp.Collections;
using MemSharp.Commands;
using MemSharp.Persistence;
using MemSharp.Protocol;
using MemSharp.Server;
using Xunit;

namespace MemSharp.Tests;

public class StreamGroupTests
{
    private static RespValue Run(MemDb db, params string[] arguments) =>
        CommandTable.Execute(new CommandContext(db, null), arguments);

    private static MemDb WithStream(TestClock? clock = null, int entries = 5)
    {
        var db = TestDb.Create(clock);
        for (int i = 1; i <= entries; i++) db.StreamAdd("jobs", ["n", i.ToString()], new StreamId(i, 0));
        return db;
    }

    [Fact]
    public void EachEntryGoesToExactlyOneConsumer()
    {
        using var db = WithStream();
        db.StreamGroupCreate("jobs", "workers", StreamId.Min);

        var first = db.StreamReadGroup("jobs", "workers", "alice", count: 2);
        var second = db.StreamReadGroup("jobs", "workers", "bob", count: 10);
        var third = db.StreamReadGroup("jobs", "workers", "alice");

        Assert.Equal(["1", "2"], first.Select(e => e["n"]));
        Assert.Equal(["3", "4", "5"], second.Select(e => e["n"]));
        Assert.Empty(third);
    }

    [Fact]
    public void DollarStartsAfterTheCurrentEnd()
    {
        using var db = WithStream();
        db.StreamGroupCreate("jobs", "late");
        Assert.Empty(db.StreamReadGroup("jobs", "late", "c"));

        db.StreamAdd("jobs", ["n", "6"]);
        Assert.Equal(["6"], db.StreamReadGroup("jobs", "late", "c").Select(e => e["n"]));
    }

    [Fact]
    public void DeliveredEntriesArePendingUntilAcknowledged()
    {
        using var db = WithStream();
        db.StreamGroupCreate("jobs", "g", StreamId.Min);
        db.StreamReadGroup("jobs", "g", "alice", count: 3);

        var summary = db.StreamPending("jobs", "g");
        Assert.Equal(3, summary.Count);
        Assert.Equal(new StreamId(1, 0), summary.Lowest);
        Assert.Equal(new StreamId(3, 0), summary.Highest);
        Assert.Equal([new KeyValuePair<string, long>("alice", 3)], summary.Consumers);

        Assert.Equal(2, db.StreamAck("jobs", "g", new StreamId(1, 0), new StreamId(2, 0), new StreamId(99, 0)));
        Assert.Equal(1, db.StreamPending("jobs", "g").Count);
    }

    [Fact]
    public void NoAckDeliversWithoutTracking()
    {
        using var db = WithStream();
        db.StreamGroupCreate("jobs", "g", StreamId.Min);
        Assert.Equal(5, db.StreamReadGroup("jobs", "g", "c", noAck: true).Count);
        Assert.Equal(0, db.StreamPending("jobs", "g").Count);
    }

    [Fact]
    public void AHistoryReadReturnsOnlyThisConsumersPendingEntries()
    {
        using var db = WithStream();
        db.StreamGroupCreate("jobs", "g", StreamId.Min);
        db.StreamReadGroup("jobs", "g", "alice", count: 2);
        db.StreamReadGroup("jobs", "g", "bob", count: 2);
        db.StreamAck("jobs", "g", new StreamId(1, 0));

        var recovered = db.StreamReadGroup("jobs", "g", "alice", after: StreamId.Min);
        Assert.Equal([new StreamId(2, 0)], recovered.Select(e => e.Id));

        // Re-reading history counts as a delivery.
        Assert.Equal(2, db.StreamPendingRange("jobs", "g", StreamId.Min, StreamId.Max, 10, "alice").Single().DeliveryCount);
    }

    [Fact]
    public void AHistoryReadOfATrimmedEntryReturnsItWithoutFields()
    {
        using var db = WithStream();
        db.StreamGroupCreate("jobs", "g", StreamId.Min);
        db.StreamReadGroup("jobs", "g", "alice", count: 1);
        db.StreamTrim("jobs", 2);

        var history = Run(db, "XREADGROUP", "GROUP", "g", "alice", "STREAMS", "jobs", "0");
        var entry = history.Items![0].Items![1].Items![0];
        Assert.Equal("1-0", entry.Items![0].Text);
        Assert.True(entry.Items[1].IsNull);
    }

    [Fact]
    public void ClaimMovesIdleEntriesToANewOwner()
    {
        var clock = new TestClock();
        using var db = WithStream(clock);
        db.StreamGroupCreate("jobs", "g", StreamId.Min);
        db.StreamReadGroup("jobs", "g", "dead", count: 2);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(db.StreamClaim("jobs", "g", "alive", TimeSpan.FromMinutes(1), [new StreamId(1, 0)]));

        clock.Advance(TimeSpan.FromMinutes(1));
        var claimed = db.StreamClaim("jobs", "g", "alive", TimeSpan.FromMinutes(1), [new StreamId(1, 0)]);
        Assert.Equal([new StreamId(1, 0)], claimed.Select(e => e.Id));

        var pending = db.StreamPendingRange("jobs", "g", StreamId.Min, StreamId.Max, 10);
        Assert.Equal("alive", pending[0].Consumer);
        Assert.Equal(2, pending[0].DeliveryCount);
        Assert.Equal(TimeSpan.Zero, pending[0].Idle);
        Assert.Equal("dead", pending[1].Consumer);
    }

    [Fact]
    public void ClaimOptionsMatchRedis()
    {
        var clock = new TestClock();
        using var db = WithStream(clock);
        db.StreamGroupCreate("jobs", "g", new StreamId(2, 0));

        // FORCE creates a pending entry for an id nobody was delivered; JUSTID leaves the count.
        var reply = Run(db, "XCLAIM", "jobs", "g", "c", "0", "1-0", "FORCE", "JUSTID", "RETRYCOUNT", "7", "IDLE", "5000", "LASTID", "4-0");
        Assert.Equal(["1-0"], reply.Items!.Select(i => i.Text));

        var pending = db.StreamPendingRange("jobs", "g", StreamId.Min, StreamId.Max, 10).Single();
        Assert.Equal(7, pending.DeliveryCount);
        Assert.Equal(TimeSpan.FromSeconds(5), pending.Idle);
        Assert.Equal(new StreamId(4, 0), db.StreamGroups("jobs").Single().LastDeliveredId);
    }

    [Fact]
    public void AutoClaimSweepsAndReportsTrimmedEntries()
    {
        var clock = new TestClock();
        using var db = WithStream(clock);
        db.StreamGroupCreate("jobs", "g", StreamId.Min);
        db.StreamReadGroup("jobs", "g", "dead");
        db.StreamTrim("jobs", 3);                  // ids 1 and 2 are gone from the stream
        clock.Advance(TimeSpan.FromMinutes(5));

        var first = db.StreamAutoClaim("jobs", "g", "alive", TimeSpan.FromMinutes(1), StreamId.Min, count: 2);
        Assert.Equal([new StreamId(1, 0), new StreamId(2, 0)], first.Deleted);
        Assert.Empty(first.Claimed);

        var rest = db.StreamAutoClaim("jobs", "g", "alive", TimeSpan.FromMinutes(1), first.NextStart, count: 10);
        Assert.Equal(StreamId.Min, rest.NextStart);
        Assert.Equal([3, 4, 5], rest.Claimed.Select(e => e.Id.Milliseconds));
        Assert.Equal(3, db.StreamPending("jobs", "g").Count);
    }

    [Fact]
    public void GroupLifecycleCommands()
    {
        using var db = WithStream();
        Assert.Equal("OK", Run(db, "XGROUP", "CREATE", "jobs", "g", "0").Text);
        Assert.StartsWith("BUSYGROUP", Run(db, "XGROUP", "CREATE", "jobs", "g", "0").Text);
        Assert.Equal(RespKind.Error, Run(db, "XGROUP", "CREATE", "absent", "g", "0").Kind);
        Assert.Equal("OK", Run(db, "XGROUP", "CREATE", "absent", "g", "$", "MKSTREAM").Text);
        Assert.Equal(MemType.Stream, db.TypeOf("absent"));

        Assert.Equal(1, Run(db, "XGROUP", "CREATECONSUMER", "jobs", "g", "c").Integer);
        Assert.Equal(0, Run(db, "XGROUP", "CREATECONSUMER", "jobs", "g", "c").Integer);
        db.StreamReadGroup("jobs", "g", "c", count: 2);
        Assert.Equal(2, Run(db, "XGROUP", "DELCONSUMER", "jobs", "g", "c").Integer);
        Assert.Equal(0, db.StreamPending("jobs", "g").Count);

        Assert.Equal("OK", Run(db, "XGROUP", "SETID", "jobs", "g", "$").Text);
        Assert.Empty(db.StreamReadGroup("jobs", "g", "c"));

        Assert.Equal(1, Run(db, "XGROUP", "DESTROY", "jobs", "g").Integer);
        Assert.StartsWith("NOGROUP", Run(db, "XREADGROUP", "GROUP", "g", "c", "STREAMS", "jobs", ">").Text);
    }

    [Fact]
    public void XInfoDescribesGroupsAndConsumers()
    {
        using var db = WithStream();
        db.StreamGroupCreate("jobs", "g", StreamId.Min);
        db.StreamReadGroup("jobs", "g", "alice", count: 2);

        var group = Run(db, "XINFO", "GROUPS", "jobs").Items!.Single().Items!.Select(i => i.Text ?? i.Integer.ToString()).ToList();
        Assert.Equal(["name", "g", "consumers", "1", "pending", "2", "last-delivered-id", "2-0"], group);

        var consumer = Run(db, "XINFO", "CONSUMERS", "jobs", "g").Items!.Single();
        Assert.Equal("alice", consumer.Items![1].Text);

        var stream = Run(db, "XINFO", "STREAM", "jobs").Items!;
        Assert.Equal(5, stream[1].Integer);
        Assert.Equal("5-0", stream[3].Text);
    }

    [Fact]
    public void XPendingWireFormats()
    {
        using var db = WithStream();
        db.StreamGroupCreate("jobs", "g", StreamId.Min);
        Assert.Equal(0, Run(db, "XPENDING", "jobs", "g").Items![0].Integer);

        db.StreamReadGroup("jobs", "g", "alice", count: 2);
        var summary = Run(db, "XPENDING", "jobs", "g").Items!;
        Assert.Equal(2, summary[0].Integer);
        Assert.Equal("1-0", summary[1].Text);
        Assert.Equal("alice", summary[3].Items![0].Items![0].Text);

        var detail = Run(db, "XPENDING", "jobs", "g", "-", "+", "10", "alice").Items!;
        Assert.Equal(2, detail.Length);
        Assert.Equal(1, detail[0].Items![3].Integer);
    }

    [Fact]
    public void XReadReturnsNewEntriesOrNil()
    {
        using var db = WithStream();
        var reply = Run(db, "XREAD", "COUNT", "2", "STREAMS", "jobs", "3-0");
        Assert.Equal("jobs", reply.Items![0].Items![0].Text);
        Assert.Equal(["4-0", "5-0"], reply.Items[0].Items![1].Items!.Select(e => e.Items![0].Text));

        Assert.True(Run(db, "XREAD", "STREAMS", "jobs", "$").IsNull);
        Assert.Equal(RespKind.Error, Run(db, "XREAD", "STREAMS", "jobs").Kind);
    }

    [Fact]
    public void GroupsSurviveASnapshot()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");
        var clock = new TestClock();

        using (var db = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path), TimeProvider = clock, ExpirySweepInterval = TimeSpan.Zero }))
        {
            for (int i = 1; i <= 5; i++) db.StreamAdd("jobs", ["n", i.ToString()], new StreamId(i, 0));
            db.StreamGroupCreate("jobs", "g", StreamId.Min);
            db.StreamReadGroup("jobs", "g", "alice", count: 3);
            db.StreamAck("jobs", "g", new StreamId(2, 0));
            db.StreamTrim("jobs", 0);
            db.Save();
        }

        using var reloaded = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path), TimeProvider = clock, ExpirySweepInterval = TimeSpan.Zero });
        var info = reloaded.StreamGroups("jobs").Single();
        Assert.Equal(new StreamId(3, 0), info.LastDeliveredId);
        Assert.Equal(2, info.Pending);
        Assert.Equal([1, 3], reloaded.StreamPendingRange("jobs", "g", StreamId.Min, StreamId.Max, 10, "alice").Select(p => p.Id.Milliseconds));

        // The last id survives trimming to empty, so an old id is still refused.
        Assert.Throws<MemSharpCommandException>(() => reloaded.StreamAdd("jobs", ["n", "x"], new StreamId(5, 0)));
    }

    [Fact]
    public void GroupsReplayFromTheAppendOnlyLogExactly()
    {
        using var temp = new TestDb.TempDirectory();
        var clock = new TestClock();
        var options = new PersistenceOptions
        {
            SnapshotPath = temp.File("db.msnap"),
            Mode = PersistenceMode.Manual,
            SaveOnShutdown = false,
            AppendOnly = new AppendOnlyOptions { Path = temp.File("db.aof") },
        };
        MemDb Open() => new(new MemDbOptions { Persistence = options, TimeProvider = clock, ExpirySweepInterval = TimeSpan.Zero });

        List<StreamPendingEntry> before;
        using (var db = Open())
        {
            for (int i = 1; i <= 6; i++) db.StreamAdd("jobs", ["n", i.ToString()], new StreamId(i, 0));
            db.StreamGroupCreate("jobs", "g", StreamId.Min);
            db.StreamGroupCreate("jobs", "fast", StreamId.Min);
            db.StreamReadGroup("jobs", "g", "alice", count: 3);
            db.StreamReadGroup("jobs", "g", "bob", count: 2);
            db.StreamReadGroup("jobs", "fast", "x", noAck: true);
            clock.Advance(TimeSpan.FromMinutes(2));
            db.StreamClaim("jobs", "g", "bob", TimeSpan.FromMinutes(1), [new StreamId(1, 0)]);
            db.StreamReadGroup("jobs", "g", "alice", after: StreamId.Min);
            db.StreamAck("jobs", "g", new StreamId(4, 0));
            before = db.StreamPendingRange("jobs", "g", StreamId.Min, StreamId.Max, 100);
        }

        // Replay happens two minutes later, which would change every idle-based decision if the
        // log held the commands rather than their effects.
        clock.Advance(TimeSpan.FromMinutes(2));
        using var recovered = Open();
        var after = recovered.StreamPendingRange("jobs", "g", StreamId.Min, StreamId.Max, 100);

        Assert.Equal(before.Select(p => (p.Id, p.Consumer, p.DeliveryCount)), after.Select(p => (p.Id, p.Consumer, p.DeliveryCount)));
        Assert.Equal(before.Select(p => p.Idle + TimeSpan.FromMinutes(2)), after.Select(p => p.Idle));
        Assert.Equal(new StreamId(5, 0), recovered.StreamGroups("jobs").Single(g => g.Name == "g").LastDeliveredId);
        Assert.Equal(new StreamId(6, 0), recovered.StreamGroups("jobs").Single(g => g.Name == "fast").LastDeliveredId);
        Assert.Equal(0, recovered.StreamGroups("jobs").Single(g => g.Name == "fast").Pending);
    }
}

public class BlockingReadTests
{
    private static async Task<(MemDb Db, MemServer Server, int Port)> StartAsync()
    {
        var db = TestDb.Create();
        var server = new MemServer(db, new MemServerOptions { Port = 0 });
        await server.StartAsync();
        return (db, server, server.EndPoint!.Port);
    }

    [Fact]
    public async Task XReadBlockWakesOnAppend()
    {
        var (db, server, port) = await StartAsync();
        await using var runningServer = server;
        using var openDb = db;
        db.StreamAdd("s", ["n", "old"]);

        await using var reader = new MemClient();
        await reader.ConnectAsync("127.0.0.1", port);

        var pending = reader.ExecuteAsync("XREAD", "BLOCK", "0", "STREAMS", "s", "$");
        await Task.Delay(200);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, db.Statistics.BlockedClients);

        db.StreamAdd("s", ["n", "new"]);
        var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        // "$" was pinned when the read started, so the entry that woke it is the one returned.
        Assert.Equal("new", reply.Items![0].Items![1].Items![0].Items![1].Items![1].Text);
        Assert.Equal(0, db.Statistics.BlockedClients);
    }

    [Fact]
    public async Task XReadGroupBlockTimesOutWithNil()
    {
        var (db, server, port) = await StartAsync();
        await using var runningServer = server;
        using var openDb = db;
        db.StreamGroupCreate("s", "g", createStream: true);

        await using var client = new MemClient();
        await client.ConnectAsync("127.0.0.1", port);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var reply = await client.ExecuteAsync("XREADGROUP", "GROUP", "g", "c", "BLOCK", "300", "STREAMS", "s", ">");
        Assert.True(reply.IsNull);
        Assert.InRange(timer.ElapsedMilliseconds, 250, 3_000);
    }

    [Fact]
    public async Task APipelinedReplyIsNotHeldBehindABlockedRead()
    {
        var (db, server, port) = await StartAsync();
        await using var runningServer = server;
        using var openDb = db;
        db.StreamGroupCreate("s", "g", createStream: true);

        await using var client = new MemClient();
        await client.ConnectAsync("127.0.0.1", port);

        var batch = client.PipelineAsync([
            ["SET", "k", "v"],
            ["XREADGROUP", "GROUP", "g", "c", "BLOCK", "0", "STREAMS", "s", ">"],
        ]);
        await Task.Delay(200);
        Assert.Equal("v", db.Get("k"));

        db.StreamAdd("s", ["n", "1"]);
        var replies = await batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("OK", replies[0].Text);
        Assert.False(replies[1].IsNull);
    }
}
