using MemSharp.Persistence;
using Xunit;

namespace MemSharp.Tests;

public class SnapshotTests
{
    [Fact]
    public void EveryTypeSurvivesARoundTrip()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");

        using (var db = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) }))
        {
            db.Set("string", "hello");
            db.ListPushRight("list", "a", "b", "c");
            db.HashSet("hash", "field", "value");
            db.SetAdd("set", "m1", "m2");
            db.SortedSetAdd("zset", "member", 12.5);
            db.TimeSeriesCreate("series", retention: 500);
            db.TimeSeriesAdd("series", 3.25, 1000);
            db.StreamAdd("stream", ["symbol", "BTC"]);
            db.Save();
        }

        using var reloaded = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) });

        Assert.Equal("hello", reloaded.Get("string"));
        Assert.Equal(["a", "b", "c"], reloaded.ListRange("list", 0, -1));
        Assert.Equal("value", reloaded.HashGet("hash", "field"));
        Assert.Equal(2, reloaded.SetLength("set"));
        Assert.Equal(12.5, reloaded.SortedSetScore("zset", "member"));
        Assert.Equal(1, reloaded.TimeSeriesLength("series"));
        Assert.Equal("BTC", reloaded.StreamRange("stream").Single()["symbol"]);
    }

    [Fact]
    public void ExpiryTimestampsSurvive()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");

        using (var db = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) }))
        {
            db.Set("volatile", "v", TimeSpan.FromHours(2));
            db.Set("permanent", "v");
            db.Save();
        }

        using var reloaded = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) });

        Assert.NotNull(reloaded.TimeToLive("volatile"));
        Assert.Null(reloaded.TimeToLive("permanent"));
    }

    [Fact]
    public void ExpiredKeysAreNotWritten()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");
        var clock = new TestClock();

        using (var db = new MemDb(new MemDbOptions
        {
            TimeProvider = clock,
            ExpirySweepInterval = TimeSpan.Zero,
            Persistence = PersistenceOptions.ManualSnapshot(path),
        }))
        {
            db.Set("gone", "v", TimeSpan.FromSeconds(1));
            db.Set("stays", "v");
            clock.Advance(TimeSpan.FromSeconds(5));
            db.Save();
        }

        using var reloaded = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) });
        Assert.Equal(1, reloaded.Count);
        Assert.True(reloaded.ContainsKey("stays"));
    }

    [Fact]
    public void CorruptSnapshotIsRejected()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");

        using (var db = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) }))
        {
            for (int i = 0; i < 50; i++) db.Set($"k{i}", "v");
            db.Save();
        }

        var bytes = File.ReadAllBytes(path);
        bytes[^5] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        // Refusing beats loading half a file: a partial load leaves the database in a state that is
        // neither the old contents nor the new.
        Assert.Throws<PersistenceException>(() =>
            new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) }));
    }

    [Fact]
    public void TruncatedSnapshotIsRejected()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");

        using (var db = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) }))
        {
            for (int i = 0; i < 50; i++) db.Set($"k{i}", "v");
            db.Save();
        }

        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.Throws<PersistenceException>(() =>
            new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) }));
    }

    [Fact]
    public void NonSnapshotFileIsRejected()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("not-a-snapshot.msnap");
        File.WriteAllText(path, "this is plain text, not a MemSharp snapshot");

        Assert.Throws<PersistenceException>(() =>
            new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) }));
    }

    [Fact]
    public void MissingSnapshotStartsEmptyRatherThanFailing()
    {
        using var temp = new TestDb.TempDirectory();
        using var db = new MemDb(new MemDbOptions
        {
            Persistence = PersistenceOptions.ManualSnapshot(temp.File("never-written.msnap")),
        });

        Assert.Equal(0, db.Count);
    }

    [Fact]
    public void SaveIsAtomicAgainstTheExistingFile()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");

        using var db = new MemDb(new MemDbOptions { Persistence = PersistenceOptions.ManualSnapshot(path) });
        db.Set("k", "v");
        db.Save();
        long firstLength = new FileInfo(path).Length;

        for (int i = 0; i < 100; i++) db.Set($"more{i}", "v");
        db.Save();

        Assert.True(new FileInfo(path).Length > firstLength);
        Assert.False(File.Exists(path + ".tmp"));       // the temporary file is moved, not left behind
    }

    [Fact]
    public void SaveWithoutAPathThrowsAClearError()
    {
        using var db = new MemDb();
        var error = Assert.Throws<InvalidOperationException>(() => db.Save());
        Assert.Contains("SnapshotPath", error.Message);
    }

    [Fact]
    public void SaveToWritesAnExplicitPath()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("explicit.msnap");

        using (var db = new MemDb())
        {
            db.Set("k", "v");
            db.SaveTo(path);
        }

        using var reloaded = new MemDb();
        reloaded.LoadFrom(path);
        Assert.Equal("v", reloaded.Get("k"));
    }
}

public class AppendOnlyTests
{
    private static PersistenceOptions Durable(string snapshot, string log, FsyncPolicy fsync = FsyncPolicy.Always) => new()
    {
        SnapshotPath = snapshot,
        Mode = PersistenceMode.Manual,
        SaveOnShutdown = false,
        AppendOnly = new AppendOnlyOptions { Path = log, Fsync = fsync },
    };

    [Fact]
    public void WritesAreRecoveredWithoutASnapshot()
    {
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log) }))
        {
            db.Set("k", "v");
            db.Increment("counter", 7);
            db.ListPushRight("list", "a", "b");
            db.HashSet("hash", "f", "v");
            db.SortedSetAdd("z", "m", 1.5);
            db.SetAdd("set", "x");
        }

        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log) });

        Assert.Equal("v", recovered.Get("k"));
        Assert.Equal("7", recovered.Get("counter"));
        Assert.Equal(["a", "b"], recovered.ListRange("list", 0, -1));
        Assert.Equal("v", recovered.HashGet("hash", "f"));
        Assert.Equal(1.5, recovered.SortedSetScore("z", "m"));
        Assert.True(recovered.SetContains("set", "x"));
    }

    [Fact]
    public void DeletionsAreRecovered()
    {
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log) }))
        {
            db.Set("keep", "v");
            db.Set("remove", "v");
            db.Delete("remove");
        }

        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log) });

        Assert.True(recovered.ContainsKey("keep"));
        Assert.False(recovered.ContainsKey("remove"));
    }

    [Fact]
    public void SavingTruncatesTheLog()
    {
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log) }))
        {
            for (int i = 0; i < 1_000; i++) db.Set($"k{i}", "value");
            long beforeSave = new FileInfo(log).Length;
            Assert.True(beforeSave > 0);

            db.Save();

            // The snapshot now covers everything the log held, so keeping the log would replay work
            // the snapshot already contains and grow without bound.
            Assert.True(new FileInfo(log).Length < beforeSave);
        }
    }

    [Fact]
    public void ATornTailIsDiscardedRatherThanFailing()
    {
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log) }))
        {
            db.Set("first", "1");
            db.Set("second", "2");
        }

        // Simulate a process that died mid-write.
        var bytes = File.ReadAllBytes(log);
        File.WriteAllBytes(log, bytes[..(bytes.Length - 6)]);

        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log) });

        // The complete commands before the tear are kept; the partial one is not corruption, it is
        // the write that was in flight when the power went.
        Assert.Equal("1", recovered.Get("first"));
    }

    [Fact]
    public void SnapshotAndLogCombineOnRestore()
    {
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log) }))
        {
            db.Set("in-snapshot", "1");
            db.Save();                       // snapshot taken, log truncated
            db.Set("after-snapshot", "2");   // only the log has this
        }

        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log) });

        Assert.Equal("1", recovered.Get("in-snapshot"));
        Assert.Equal("2", recovered.Get("after-snapshot"));
    }

    [Theory]
    [InlineData(FsyncPolicy.Never)]
    [InlineData(FsyncPolicy.EverySecond)]
    [InlineData(FsyncPolicy.Always)]
    public void EveryFsyncPolicyRecoversOnCleanShutdown(FsyncPolicy policy)
    {
        using var temp = new TestDb.TempDirectory();
        string snapshot = temp.File("db.msnap"), log = temp.File("db.aof");

        using (var db = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log, policy) }))
        {
            for (int i = 0; i < 100; i++) db.Set($"k{i}", i.ToString());
        }

        using var recovered = new MemDb(new MemDbOptions { Persistence = Durable(snapshot, log, policy) });
        Assert.Equal("99", recovered.Get("k99"));
    }
}

public class PersistenceModeTests
{
    [Fact]
    public void ManualModeDoesNotWriteUntilAsked()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");

        using var db = new MemDb(new MemDbOptions
        {
            Persistence = new PersistenceOptions
            {
                SnapshotPath = path,
                Mode = PersistenceMode.Manual,
                SaveOnShutdown = false,
            },
        });

        for (int i = 0; i < 100; i++) db.Set($"k{i}", "v");
        Assert.False(File.Exists(path));

        db.Save();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AutomaticModeSavesOnTheChangeThreshold()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");

        using var db = new MemDb(new MemDbOptions
        {
            Persistence = PersistenceOptions.AutomaticSnapshot(path, TimeSpan.FromHours(1), changes: 10),
        });

        for (int i = 0; i < 50; i++) db.Set($"k{i}", "v");

        // The threshold save runs on a background thread, so give it a bounded window rather than
        // asserting immediately.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (db.LastSaveTime is null && DateTime.UtcNow < deadline) Thread.Sleep(25);

        Assert.NotNull(db.LastSaveTime);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SaveOnShutdownWritesWithoutAnExplicitCall()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");

        using (var db = new MemDb(new MemDbOptions
        {
            Persistence = new PersistenceOptions
            {
                SnapshotPath = path,
                Mode = PersistenceMode.Manual,
                SaveOnShutdown = true,
            },
        }))
        {
            db.Set("k", "v");
        }

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void NoneModeNeverTouchesTheDisk()
    {
        using var temp = new TestDb.TempDirectory();
        string path = temp.File("db.msnap");

        using (var db = new MemDb(new MemDbOptions
        {
            Persistence = new PersistenceOptions { SnapshotPath = path, Mode = PersistenceMode.None },
        }))
        {
            db.Set("k", "v");
        }

        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(PersistenceMode.Manual)]
    [InlineData(PersistenceMode.Automatic)]
    public void ModeWithoutAPathIsRejectedAtConstruction(PersistenceMode mode)
    {
        var options = new MemDbOptions { Persistence = new PersistenceOptions { Mode = mode } };

        // Failing here beats discovering at the first save that there was nowhere to write.
        Assert.Throws<ArgumentException>(() => new MemDb(options));
    }

    [Fact]
    public void AutomaticModeWithNoTriggerIsRejected()
    {
        var options = new MemDbOptions
        {
            Persistence = new PersistenceOptions
            {
                SnapshotPath = "x.msnap",
                Mode = PersistenceMode.Automatic,
                AutoSaveInterval = TimeSpan.Zero,
                AutoSaveAfterChanges = 0,
            },
        };

        Assert.Throws<ArgumentException>(() => new MemDb(options));
    }
}
