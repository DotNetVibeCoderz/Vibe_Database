namespace Faiss.Net.Core;

/// <summary>
/// The detect-and-repair loop behind <c>AddLevel0LinksFromSample</c>, after upstream FAISS #5705.
/// Works through an <see cref="HnswSession"/> only, so it serves every HNSW storage and the binary
/// HNSW alike.
/// <list type="number">
/// <item>Search every sample; count how many land on each node.</item>
/// <item>Nodes receiving at least <c>MinMeanFactor</c> times the mean are candidate sinks.</item>
/// <item>Samples that landed on a candidate are searched exhaustively. A candidate is a sink when a
/// strictly nearer node exists (beyond a relative tolerance) for at least <c>MinCloserFraction</c>
/// of its samples.</item>
/// <item>Each sink gets level-0 links to the true nearest nodes of its wrong samples, most frequent
/// first: free slots first, then in place of its farthest links, keeping at least half of its own.</item>
/// <item>Repeat until no sink remains, nothing changes, or <c>MaxRounds</c> is reached.</item>
/// </list>
/// Searches run in parallel; graph edits run on one thread in node order, so the outcome is
/// independent of the thread count.
/// </summary>
internal sealed class HnswSinkRepair(
    HnswGraph graph, HnswSession session, nint samples, int n, int strideBytes, int ef,
    HNSWLevel0LinkParameters parameters, Func<HnswScratch> rent, Action<HnswScratch> release, int threads)
{
    public void Run(HNSWLevel0LinkStats stats)
    {
        for (int round = 0; round < parameters.MaxRounds; round++)
        {
            var sinks = Detect(stats);
            if (round == 0) stats.SinksFound = sinks.Count;
            if (sinks.Count == 0)
            {
                stats.SinksRemaining = 0;
                return;
            }

            bool changed = false;
            foreach (var (sink, targets) in sinks) changed |= Relink(sink, targets, stats);
            if (!changed)
            {
                stats.SinksRemaining = sinks.Count;
                return;
            }
            stats.Rounds++;
        }
        stats.SinksRemaining = Detect(stats).Count;
    }

    /// <summary>Returns each sink with the true nearest nodes of its wrong samples, in node order.</summary>
    private SortedDictionary<int, Dictionary<int, int>> Detect(HNSWLevel0LinkStats stats)
    {
        var (entry, maxLevel) = graph.Entry;
        var assigned = new int[n];
        var scores = new float[n];

        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            var scratch = rent();
            try
            {
                var found = session.Search(graph, samples + (nint)i * strideBytes, entry, maxLevel, ef, scratch, null);
                assigned[i] = found.Count > 0 ? found[0].Node : -1;
                scores[i] = found.Count > 0 ? found[0].Score : float.MaxValue;
            }
            finally
            {
                release(scratch);
            }
        });

        var counts = new int[session.Limit];
        foreach (int node in assigned) if (node >= 0) counts[node]++;
        double threshold = parameters.MinMeanFactor * n / Math.Max(1, session.Limit);

        var candidateSamples = new List<int>();
        for (int i = 0; i < n; i++)
            if (assigned[i] >= 0 && counts[assigned[i]] >= threshold && counts[assigned[i]] > 1)
                candidateSamples.Add(i);

        var truth = new (int Node, float Score)[candidateSamples.Count];
        Parallel.For(0, candidateSamples.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, j =>
        {
            var scratch = rent();
            try
            {
                truth[j] = session.ExactNearest(samples + (nint)candidateSamples[j] * strideBytes, scratch);
            }
            finally
            {
                release(scratch);
            }
        });
        stats.ExactSearches += candidateSamples.Count;

        var wrong = new Dictionary<int, Dictionary<int, int>>();
        var wrongCounts = new Dictionary<int, int>();
        for (int j = 0; j < candidateSamples.Count; j++)
        {
            int i = candidateSamples[j];
            int node = assigned[i];
            var (best, bestScore) = truth[j];
            double tolerance = parameters.RelativeTolerance * Math.Max(Math.Abs(scores[i]), 1e-30);
            if (best < 0 || best == node || bestScore >= scores[i] - tolerance) continue;

            wrongCounts[node] = wrongCounts.GetValueOrDefault(node) + 1;
            if (!wrong.TryGetValue(node, out var targets)) wrong[node] = targets = [];
            targets[best] = targets.GetValueOrDefault(best) + 1;
        }

        var sinks = new SortedDictionary<int, Dictionary<int, int>>();
        foreach (var (node, wrongCount) in wrongCounts)
            if (wrongCount >= parameters.MinCloserFraction * counts[node])
                sinks[node] = wrong[node];
        return sinks;
    }

    /// <summary>Gives one sink links to its most frequent true neighbours. Returns whether anything changed.</summary>
    private bool Relink(int sink, Dictionary<int, int> targets, HNSWLevel0LinkStats stats)
    {
        var slots = graph.Neighbors(sink, 0);
        var linked = new HashSet<int>();
        int original = 0;
        foreach (int neighbor in slots)
        {
            if (neighbor == HnswGraph.NoNeighbor) break;
            linked.Add(neighbor);
            original++;
        }

        var ordered = targets
            .Where(t => t.Key != sink && !linked.Contains(t.Key))
            .OrderByDescending(t => t.Value).ThenBy(t => t.Key)
            .Take(parameters.MaxLinksPerSink)
            .Select(t => t.Key)
            .ToList();
        if (ordered.Count == 0) return false;

        var scratch = rent();
        try
        {
            lock (graph.LockFor(sink))
            {
                int used = original;
                int next = 0;
                for (; next < ordered.Count && used < slots.Length; next++)
                {
                    slots[used++] = ordered[next];
                    stats.LinksAdded++;
                }

                if (next < ordered.Count)
                {
                    // Replace the farthest original links, but keep at least half of them.
                    var byDistance = new List<(int Slot, float Score)>(original);
                    for (int i = 0; i < original; i++) byDistance.Add((i, session.Pair(sink, slots[i], scratch)));
                    byDistance.Sort(static (a, b) => b.Score.CompareTo(a.Score));
                    int replaceable = original - (original + 1) / 2;
                    for (int r = 0; r < replaceable && next < ordered.Count; r++, next++)
                    {
                        slots[byDistance[r].Slot] = ordered[next];
                        stats.LinksReplaced++;
                    }
                }
            }
        }
        finally
        {
            release(scratch);
        }
        return true;
    }
}
