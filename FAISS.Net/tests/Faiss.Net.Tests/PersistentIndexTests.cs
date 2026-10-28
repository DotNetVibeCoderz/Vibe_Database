using Faiss.Net.IO;
using Xunit;

namespace Faiss.Net.Tests;

/// <summary>Snapshot-plus-journal persistence, including the crash cases it exists for.</summary>
public sealed class PersistentIndexTests : IDisposable
{
    private const int D = 16;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "faissnet-journal-" + Guid.NewGuid().ToString("N"));

    public PersistentIndexTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private string IndexPath => Path.Combine(_directory, "vectors.index");

    private static Index NewIndex() => new IndexIDMap2(new IndexFlatL2(D));

    [Fact]
    public void OperationsSurviveReopenWithoutACheckpoint()
    {
        var data = TestData.Clustered(1000, D);
        var queries = TestData.Slice(data, D, 10);
        SearchResult expected;
        using (var index = PersistentIndex.Open(IndexPath, NewIndex))
        {
            index.AddWithIds(data.AsSpan(0, 600 * D), Enumerable.Range(0, 600).Select(i => 10L * i).ToArray());
            index.AddWithIds(data.AsSpan(600 * D), Enumerable.Range(600, 400).Select(i => 10L * i).ToArray());
            index.RemoveIds([0, 10, 20]);
            expected = index.Search(queries, 5);
        }

        using var reopened = PersistentIndex.Open(IndexPath, () => throw new InvalidOperationException("must load, not create"));
        Assert.Equal(3, reopened.ReplayedRecords);
        Assert.Equal(997, reopened.Ntotal);
        Assert.Equal(expected.Labels, reopened.Search(queries, 5).Labels);
    }

    [Fact]
    public void CheckpointFoldsTheJournalIntoTheSnapshot()
    {
        var data = TestData.Clustered(500, D);
        using (var index = PersistentIndex.Open(IndexPath, NewIndex))
        {
            index.AddWithIds(data, Enumerable.Range(0, 500).Select(i => (long)i).ToArray());
            long before = index.JournalBytes;
            index.Checkpoint();
            Assert.True(index.JournalBytes < before);
        }
        using var reopened = PersistentIndex.Open(IndexPath, NewIndex);
        Assert.Equal(0, reopened.ReplayedRecords);
        Assert.Equal(500, reopened.Ntotal);
    }

    [Fact]
    public void ATornFinalRecordIsDiscardedAndTheRestReplayed()
    {
        var data = TestData.Clustered(300, D);
        using (var index = PersistentIndex.Open(IndexPath, NewIndex))
        {
            index.AddWithIds(data.AsSpan(0, 100 * D), Enumerable.Range(0, 100).Select(i => (long)i).ToArray());
            index.AddWithIds(data.AsSpan(100 * D), Enumerable.Range(100, 200).Select(i => (long)i).ToArray());
        }

        // Simulate a crash in the middle of writing the second record.
        string journal = IndexPath + ".journal";
        long length = new FileInfo(journal).Length;
        using (var stream = new FileStream(journal, FileMode.Open)) stream.SetLength(length - 17);

        using (var reopened = PersistentIndex.Open(IndexPath, NewIndex))
        {
            Assert.True(reopened.RecoveredFromTornWrite);
            Assert.Equal(1, reopened.ReplayedRecords);
            Assert.Equal(100, reopened.Ntotal);
            // New writes append after the last good record.
            reopened.AddWithIds(data.AsSpan(100 * D, 50 * D), Enumerable.Range(100, 50).Select(i => (long)i).ToArray());
        }
        using var again = PersistentIndex.Open(IndexPath, NewIndex);
        Assert.False(again.RecoveredFromTornWrite);
        Assert.Equal(150, again.Ntotal);
    }

    [Fact]
    public void AJournalOlderThanItsSnapshotIsNotReplayedTwice()
    {
        var data = TestData.Clustered(200, D);
        string journal = IndexPath + ".journal";
        byte[] staleJournal;
        using (var index = PersistentIndex.Open(IndexPath, NewIndex))
        {
            index.AddWithIds(data, Enumerable.Range(0, 200).Select(i => (long)i).ToArray());
            index.Dispose();
            staleJournal = File.ReadAllBytes(journal);
        }
        using (var index = PersistentIndex.Open(IndexPath, NewIndex)) index.Checkpoint();

        // Crash after the snapshot was replaced but before the journal was reset.
        File.WriteAllBytes(journal, staleJournal);
        using var reopened = PersistentIndex.Open(IndexPath, NewIndex);
        Assert.Equal(0, reopened.ReplayedRecords);
        Assert.Equal(200, reopened.Ntotal);
    }

    [Fact]
    public void LargeJournalsCheckpointAutomatically()
    {
        var data = TestData.Clustered(2000, D);
        using var index = PersistentIndex.Open(IndexPath, () => new IndexFlatL2(D));
        index.CheckpointAfterBytes = 20_000;
        index.DurableWrites = false;
        for (int start = 0; start < 2000; start += 100) index.Add(data.AsSpan(start * D, 100 * D));
        Assert.True(index.JournalBytes <= 20_000 + 100 * D * sizeof(float) + 64);
        Assert.Throws<NotSupportedException>(() => index.RemoveIds(id => id < 5));
    }
}
