using System.Collections.Concurrent;
using BookAggregator.Api.Models;

namespace BookAggregator.Api.Services;

public sealed class ProviderStatistics
{
    private readonly ConcurrentDictionary<string, Counters> _counters = new(StringComparer.Ordinal);

    public void Record(string source, TimeSpan elapsed, bool success, bool cancelled = false)
    {
        var counters = _counters.GetOrAdd(source, _ => new Counters());
        // One lock makes updates AND each provider's snapshot internally consistent.
        lock (counters)
        {
            counters.Total++;
            counters.TotalMilliseconds += elapsed.TotalMilliseconds;
            if (cancelled) counters.Cancelled++;
            else if (success) counters.Successful++;
            else counters.Failed++;

            var bucket = elapsed.TotalMilliseconds switch
            {
                < 500 => counters.Fast,
                < 2000 => counters.Average,
                _ => counters.Slow
            };
            bucket.Count++;
            bucket.TotalMilliseconds += elapsed.TotalMilliseconds;
        }
    }

    public IReadOnlyList<ProviderStatisticsDto> Snapshot(IEnumerable<string> sources) => sources
        .Order(StringComparer.Ordinal)
        .Select(source =>
        {
            var counters = _counters.GetOrAdd(source, _ => new Counters());
            lock (counters)
            {
                return new ProviderStatisticsDto(source, counters.Total, counters.Successful,
                    counters.Failed, counters.Cancelled,
                    Mean(counters.TotalMilliseconds, counters.Total),
                    counters.Fast.Count, counters.Average.Count, counters.Slow.Count,
                    counters.Fast.MeanMilliseconds, counters.Average.MeanMilliseconds, counters.Slow.MeanMilliseconds);
            }
        }).ToArray();

    private static double Mean(double totalMilliseconds, long count) =>
        count == 0 ? 0 : Math.Round(totalMilliseconds / count, 2);

    private sealed class Bucket
    {
        public long Count;
        public double TotalMilliseconds;
        public double MeanMilliseconds => Mean(TotalMilliseconds, Count);
    }

    private sealed class Counters
    {
        public long Total, Successful, Failed, Cancelled;
        public Bucket Fast { get; } = new();
        public Bucket Average { get; } = new();
        public Bucket Slow { get; } = new();
        public double TotalMilliseconds;
    }
}
