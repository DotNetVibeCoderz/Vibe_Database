using MemSharp.Collections;
using MemSharp.Persistence;
using Xunit;

namespace MemSharp.Tests;

/// <summary>
/// The persistence faults found while building 1.1, each pinned by a test that failed before its fix.
/// </summary>
public class DurabilityTests
{
    private static PersistenceOptions Durable(string snapshot, string log) => new()
    {
        SnapshotPath = snapshot,
        Mode = PersistenceMode.Manual,
        SaveOnShutdown = false,   // recovery must come from snapshot + log, not a parting save
        AppendOnly = new AppendOnlyOptions { Path = log, Fsync = FsyncPolicy.Never },
    };

    [Fact]
    public void ConcurrentAppendsToOneStreamReplayInOrder()
    {
        // Logging after releasing the shard lock let two writers reach the log in the opposite
        // order from the one they appended in. A stream refuses an id below its head, so the replay
        // threw and the whole restore failed.
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), ExpirySweepInterval = TimeSpan.Zero }))
        {
            Parallel.For(0, 8, _ =>
            {
                for (int i = 0; i < 2_000; i++) db.StreamAdd("s", ["n", "1"]);
            });
            Parallel.For(0, 8, t =>
            {
                for (int i = 0; i < 2_000; i++) db.TimeSeriesAdd("ts", t, timestamp: null);
            });
        }

        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), ExpirySweepInterval = TimeSpan.Zero });
        Assert.Equal(16_000, recovered.StreamLength("s"));
        Assert.Equal(16_000, recovered.TimeSeriesLength("ts"));
    }

    [Fact]
    public void ConcurrentOverwritesReplayToTheLiveValue()
    {
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");
        var live = new string?[50];

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), ExpirySweepInterval = TimeSpan.Zero }))
        {
            Parallel.For(0, 8, t =>
            {
                for (int i = 0; i < 5_000; i++) db.Set($"k{i % 50}", $"{t}:{i}");
            });
            for (int k = 0; k < 50; k++) live[k] = db.Get($"k{k}");
        }

        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), ExpirySweepInterval = TimeSpan.Zero });
        for (int k = 0; k < 50; k++) Assert.Equal(live[k], recovered.Get($"k{k}"));
    }

    [Fact]
    public void WritesDuringASnapshotSurviveTheLogTruncation()
    {
        // 1.0 truncated the whole log after every snapshot. A write landing on a shard the snapshot
        // had already serialised was in neither file afterwards. INCR is the probe because it is not
        // idempotent: a lost write and a write replayed twice both show up as a wrong count.
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");
        const int Keys = 512, Rounds = 400;

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), ShardCount = 64, ExpirySweepInterval = TimeSpan.Zero }))
        {
            for (int k = 0; k < Keys; k++) db.HashSet($"pad{k}", "f", new string('x', 16_000));   // make each save take a while

            var writer = Task.Run(() =>
            {
                for (int round = 0; round < Rounds; round++)
                {
                    for (int k = 0; k < Keys; k++) db.Increment($"n{k}");
                }
            });
            int saves = 0;
            while (!writer.IsCompleted)
            {
                db.Save();
                saves++;
            }
            writer.Wait();
            Assert.True(saves > 2, $"only {saves} saves overlapped the writer; the test proved nothing");
        }

        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), ShardCount = 64, ExpirySweepInterval = TimeSpan.Zero });
        for (int k = 0; k < Keys; k++) Assert.Equal(Rounds.ToString(), recovered.Get($"n{k}"));
    }

    [Fact]
    public void ReplayedExpiryIsAbsoluteNotRestarted()
    {
        // SET ... PX was logged with the relative lifetime, so every restart granted a fresh one.
        var clock = new TestClock();
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), TimeProvider = clock, ExpirySweepInterval = TimeSpan.Zero }))
        {
            db.Set("session", "x", TimeSpan.FromMinutes(10));
            db.SetIfAbsent("lock", "y", TimeSpan.FromMinutes(10));
        }

        clock.Advance(TimeSpan.FromMinutes(9));
        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), TimeProvider = clock, ExpirySweepInterval = TimeSpan.Zero });

        Assert.Equal(TimeSpan.FromMinutes(1), recovered.TimeToLive("session"));

        // SET NX with a TTL used to replay as a plain SET - the lock came back permanent.
        Assert.Equal(TimeSpan.FromMinutes(1), recovered.TimeToLive("lock"));
    }

    [Fact]
    public void FlushDuringWritesIsOnePointInTheLog()
    {
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), ExpirySweepInterval = TimeSpan.Zero }))
        {
            for (int i = 0; i < 100; i++) db.Set($"before{i}", "x");
            db.Save();
            db.Clear();
            db.Set("after", "y");
        }

        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log), ExpirySweepInterval = TimeSpan.Zero });
        Assert.Equal(1, recovered.Count);
        Assert.Equal("y", recovered.Get("after"));
    }

    [Fact]
    public void AStreamTrimmedEmptyStillRefusesAnOldId()
    {
        using var db = TestDb.Create();
        db.StreamAdd("s", ["a", "1"], new StreamId(5, 0));
        db.StreamTrim("s", 0);

        Assert.Throws<MemSharpCommandException>(() => db.StreamAdd("s", ["a", "2"], new StreamId(4, 0)));
        Assert.Equal(new StreamId(6, 0), db.StreamAdd("s", ["a", "3"], new StreamId(6, 0)));
    }

    [Fact]
    public void AVersionOneSnapshotStillLoads()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("v1.msnap");

        // Hand-built in the 1.0 layout: one string and one stream, with no group section.
        var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)MemType.String);
            writer.Write("greeting");
            writer.Write(0L);
            writer.Write("hello");

            writer.Write((byte)MemType.Stream);
            writer.Write("events");
            writer.Write(0L);
            writer.Write(1);                  // entries
            writer.Write(1000L);
            writer.Write(0L);
            writer.Write(2);                  // fields
            writer.Write("k");
            writer.Write("v");
        }
        byte[] payload = body.ToArray();

        using (var file = new BinaryWriter(File.Create(path)))
        {
            file.Write("MEMSHRP1"u8);
            file.Write(1);                    // format version 1
            file.Write(0);
            file.Write(2L);
            file.Write(SnapshotFormat.Hash(payload));
            file.Write(payload);
        }

        using var db = TestDb.Create();
        db.LoadFrom(path);
        Assert.Equal("hello", db.Get("greeting"));
        Assert.Equal("v", db.StreamRange("events").Single()["k"]);
        Assert.Empty(db.StreamGroups("events"));
    }
}
