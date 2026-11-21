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

            if (elapsed.TotalMilliseconds < 500) counters.Fast++;
            else if (elapsed.TotalMilliseconds < 2000) counters.Average++;
            else counters.Slow++;
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
                    counters.Total == 0 ? 0 : Math.Round(counters.TotalMilliseconds / counters.Total, 2),
                    counters.Fast, counters.Average, counters.Slow);
            }
        }).ToArray();

    private sealed class Counters
    {
        public long Total, Successful, Failed, Cancelled, Fast, Average, Slow;
        public double TotalMilliseconds;
    }
}
