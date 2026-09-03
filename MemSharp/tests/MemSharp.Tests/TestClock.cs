namespace MemSharp.Tests;

/// <summary>
/// A clock the test drives by hand.
/// </summary>
/// <remarks>
/// TTL behaviour is otherwise only testable by sleeping, which makes a suite slow and flaky in
/// equal measure. <see cref="MemDbOptions.TimeProvider"/> exists precisely so this can be
/// substituted.
/// </remarks>
internal sealed class TestClock(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal static class TestDb
{
    /// <summary>A database with the background sweeper off, so tests observe only lazy expiry.</summary>
    public static MemDb Create(TimeProvider? clock = null, int shards = 8) => new(new MemDbOptions
    {
        ShardCount = shards,
        TimeProvider = clock ?? TimeProvider.System,
        ExpirySweepInterval = TimeSpan.Zero,
    });

    /// <summary>A temporary directory that deletes itself.</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "memsharp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A file handle outliving the test is not itself a test failure.
            }
        }
    }
}
