using BookAggregator.Api.Services;

namespace BookAggregator.Tests;

public sealed class StatisticsTests
{
    [Fact]
    public void RegisteredProvidersHaveZeroStatisticsBeforeAnyTraffic()
    {
        var snapshots = new ProviderStatistics().Snapshot(["C", "A", "B"]);
        Assert.Equal(["A", "B", "C"], snapshots.Select(snapshot => snapshot.Source));
        Assert.All(snapshots, snapshot =>
        {
            Assert.Equal(0, snapshot.TotalRequests);
            Assert.Equal(0, snapshot.AverageResponseTimeMs);
        });
    }

    [Fact]
    public void BucketBoundariesMeanAndOutcomeCountsAreCorrect()
    {
        var stats = new ProviderStatistics();
        stats.Record("A", TimeSpan.FromMilliseconds(499), true);
        stats.Record("A", TimeSpan.FromMilliseconds(500), false);
        stats.Record("A", TimeSpan.FromMilliseconds(1999), false, cancelled: true);
        stats.Record("A", TimeSpan.FromMilliseconds(2000), true);
        var result = Assert.Single(stats.Snapshot(["A"]));
        Assert.Equal(4, result.TotalRequests);
        Assert.Equal(1249.5, result.AverageResponseTimeMs);
        Assert.Equal(1, result.FastRequests);
        Assert.Equal(2, result.AverageRequests);
        Assert.Equal(1, result.SlowRequests);
        Assert.Equal(2, result.SuccessfulRequests);
        Assert.Equal(1, result.FailedRequests);
        Assert.Equal(1, result.CancelledRequests);
    }

    [Fact]
    public void ConcurrentUpdatesAndSnapshotsHaveNoLostUpdatesOrInconsistentTotals()
    {
        var stats = new ProviderStatistics();
        Parallel.For(0, 10000, i =>
        {
            stats.Record("A", TimeSpan.FromMilliseconds(100), i % 2 == 0);
            var snapshot = Assert.Single(stats.Snapshot(["A"]));
            Assert.Equal(snapshot.TotalRequests, snapshot.FastRequests + snapshot.AverageRequests + snapshot.SlowRequests);
            Assert.Equal(snapshot.TotalRequests, snapshot.SuccessfulRequests + snapshot.FailedRequests + snapshot.CancelledRequests);
        });
        var result = Assert.Single(stats.Snapshot(["A"]));
        Assert.Equal(10000, result.TotalRequests);
        Assert.Equal(5000, result.SuccessfulRequests);
        Assert.Equal(5000, result.FailedRequests);
        Assert.Equal(100, result.AverageResponseTimeMs);
    }
}
