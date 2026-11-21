using BookAggregator.Api.Configuration;
using BookAggregator.Api.Models;

namespace BookAggregator.Tests;

public sealed class CacheTests
{
    [Fact]
    public async Task ConcurrentIdenticalRequestsFetchOnceAndTrimQuery()
    {
        var release = TestData.Signal();
        var entered = TestData.Signal();
        var provider = new StubProvider("A", async (query, token) =>
        {
            Assert.Equal("x", query);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return [TestData.Book()];
        });
        using var cache = TestData.Cache();
        var requests = Enumerable.Range(0, 50).Select(_ => cache.SearchAsync(provider, " x ", default)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        var results = await Task.WhenAll(requests);
        Assert.Equal(1, provider.Calls);
        Assert.All(results, result => Assert.Single(result.Books));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ProvidersAndDistinctQueriesHaveSeparateEntries()
    {
        using var cache = TestData.Cache();
        var a = StubProvider.Returning("A", TestData.Book());
        var b = StubProvider.Returning("B", TestData.Book(source: "B"));
        await cache.SearchAsync(a, "x", default);
        await cache.SearchAsync(b, "x", default);
        await cache.SearchAsync(a, "y", default);
        await cache.SearchAsync(a, "X", default);
        Assert.Equal(3, a.Calls);
        Assert.Equal(1, b.Calls);
    }

    [Fact]
    public async Task ExpiredFreshEntryRefreshesAndSuccessfulEmptyResultsAreCached()
    {
        var clock = new ManualClock();
        using var cache = TestData.Cache(clock);
        var provider = StubProvider.Returning("A");
        await cache.SearchAsync(provider, "x", default);
        clock.Advance(TimeSpan.FromSeconds(119));
        await cache.SearchAsync(provider, "x", default);
        Assert.Equal(1, provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await cache.SearchAsync(provider, "x", default);
        Assert.Equal(2, provider.Calls);
        Assert.Empty(result.Books);
        Assert.False(result.IsUnavailable);
    }

    [Fact]
    public async Task FailedRefreshUsesStaleDataBacksOffAndRecovers()
    {
        var clock = new ManualClock();
        var healthy = true;
        var provider = new StubProvider("A", (_, _) => healthy
            ? Task.FromResult<IReadOnlyList<BookDto>>([TestData.Book()]) : throw new HttpRequestException());
        using var cache = TestData.Cache(clock);
        await cache.SearchAsync(provider, "x", default);
        clock.Advance(TimeSpan.FromSeconds(120));
        healthy = false;
        var stale = await cache.SearchAsync(provider, "x", default);
        Assert.Single(stale.Books);
        Assert.True(stale.IsStale);
        Assert.True(stale.IsUnavailable);
        await cache.SearchAsync(provider, "x", default);
        Assert.Equal(2, provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(10));
        healthy = true;
        var recovered = await cache.SearchAsync(provider, "x", default);
        Assert.Equal(3, provider.Calls);
        Assert.False(recovered.IsStale);
        Assert.False(recovered.IsUnavailable);
    }

    [Fact]
    public async Task RepeatedFailuresNeverExtendOriginalStaleDeadline()
    {
        var clock = new ManualClock();
        var healthy = true;
        var provider = new StubProvider("A", (_, _) => healthy
            ? Task.FromResult<IReadOnlyList<BookDto>>([TestData.Book()]) : throw new HttpRequestException());
        using var cache = TestData.Cache(clock);
        await cache.SearchAsync(provider, "x", default);
        healthy = false;
        clock.Advance(TimeSpan.FromSeconds(899));
        Assert.True((await cache.SearchAsync(provider, "x", default)).IsStale);
        clock.Advance(TimeSpan.FromSeconds(1));
        var expired = await cache.SearchAsync(provider, "x", default);
        Assert.Empty(expired.Books);
        Assert.False(expired.IsStale);
        Assert.True(expired.IsUnavailable);
        Assert.Equal(2, provider.Calls); // still respects the short failure backoff
        clock.Advance(TimeSpan.FromSeconds(9));
        await cache.SearchAsync(provider, "x", default);
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task UnavailableProviderBackoffDoesNotPretendToBeSuccessfulEmptySearch()
    {
        var clock = new ManualClock();
        using var cache = TestData.Cache(clock);
        var provider = StubProvider.Failing("A");
        var first = await cache.SearchAsync(provider, "x", default);
        var second = await cache.SearchAsync(provider, "x", default);
        Assert.True(first.IsUnavailable);
        Assert.True(second.IsUnavailable);
        Assert.Equal(1, provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(10));
        await cache.SearchAsync(provider, "x", default);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelOwnerOrOtherWaiters()
    {
        var release = TestData.Signal();
        var provider = new StubProvider("A", async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return [TestData.Book()];
        });
        using var cache = TestData.Cache();
        var owner = cache.SearchAsync(provider, "x", default);
        using var cts = new CancellationTokenSource();
        var waiter = cache.SearchAsync(provider, "x", cts.Token);
        var other = cache.SearchAsync(provider, "x", default);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.TrySetResult();
        Assert.Single((await owner).Books);
        Assert.Single((await other).Books);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task CancelledOwnerDoesNotPoisonCacheAndWaitingCallerCanFetch()
    {
        var count = 0;
        var provider = new StubProvider("A", async (_, token) =>
        {
            if (Interlocked.Increment(ref count) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [TestData.Book()];
        });
        using var cache = TestData.Cache();
        using var cts = new CancellationTokenSource();
        var owner = cache.SearchAsync(provider, "x", cts.Token);
        var waiter = cache.SearchAsync(provider, "x", default);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.Single((await waiter).Books);
        Assert.False((await cache.SearchAsync(provider, "x", default)).IsUnavailable);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledRequestCannotReadCachedResult()
    {
        using var cache = TestData.Cache();
        var provider = StubProvider.Returning("A", TestData.Book());
        await cache.SearchAsync(provider, "x", default);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SearchAsync(provider, "x", cts.Token));
        Assert.Equal(1, provider.Calls);
    }
}
