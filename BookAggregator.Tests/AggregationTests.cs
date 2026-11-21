using BookAggregator.Api.Models;
using BookAggregator.Api.Services;

namespace BookAggregator.Tests;

public sealed class AggregationTests
{
    [Fact]
    public async Task AllProvidersStartBeforeAnyCompletes()
    {
        var allStarted = TestData.Signal();
        var release = TestData.Signal();
        var started = 0;
        var providers = Enumerable.Range(1, 3).Select(i => new StubProvider($"P{i}", async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 3) allStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return [TestData.Book(source: $"P{i}")];
        })).ToArray();
        using var cache = TestData.Cache();
        var task = new BookAggregationService(providers, cache).SearchAsync(new BookSearchRequest { Query = "x" }, default);
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var result = await task;
        Assert.Equal(3, result.Books.Count);
        Assert.True(result.HasUsableProvider);
    }

    [Fact]
    public async Task PartialFailureKeepsHealthyResultsAndMarksMissingSource()
    {
        using var cache = TestData.Cache();
        var service = new BookAggregationService([
            StubProvider.Returning("A", TestData.Book()), StubProvider.Failing("B"),
            StubProvider.Returning("C")], cache);
        var result = await service.SearchAsync(new BookSearchRequest { Query = "x" }, default);
        Assert.Single(result.Books);
        Assert.Equal(["B"], result.UnavailableProviders);
        Assert.True(result.HasUsableProvider);
    }

    [Fact]
    public async Task AllOutagesAreDistinctFromSuccessfulEmptySearch()
    {
        using var cache = TestData.Cache();
        var failed = await new BookAggregationService([StubProvider.Failing("A")], cache)
            .SearchAsync(new BookSearchRequest { Query = "x" }, default);
        Assert.False(failed.HasUsableProvider);
        var empty = await new BookAggregationService([StubProvider.Returning("B")], cache)
            .SearchAsync(new BookSearchRequest { Query = "x" }, default);
        Assert.True(empty.HasUsableProvider);
        Assert.Empty(empty.Books);
    }

    [Fact]
    public async Task SourceSelectionAvoidsUnnecessaryProviderCalls()
    {
        using var cache = TestData.Cache();
        var a = StubProvider.Returning("A", TestData.Book());
        var b = StubProvider.Returning("B", TestData.Book(source: "B"));
        var result = await new BookAggregationService([a, b], cache)
            .SearchAsync(new BookSearchRequest { Query = "x", Source = " b " }, default);
        Assert.Equal("B", Assert.Single(result.Books).Source);
        Assert.Equal(0, a.Calls);
        Assert.Equal(1, b.Calls);
    }

    [Fact]
    public async Task FiltersAreCaseInsensitiveCombinedAndDoNotPolluteCache()
    {
        using var cache = TestData.Cache();
        var provider = StubProvider.Returning("A", TestData.Book("1", year: 1818),
            TestData.Book("2", year: 1820), TestData.Book("3", year: null),
            TestData.Book("4", year: 1818, authors: ["Someone Else"]),
            TestData.Book("5", year: 1818, categories: ["Science"]));
        var service = new BookAggregationService([provider], cache);
        var result = await service.SearchAsync(new BookSearchRequest
        {
            Query = "x",
            Author = " SHELLEY ",
            Category = " fict ",
            MinYear = 1818,
            MaxYear = 1818
        }, default);
        Assert.Equal("1", Assert.Single(result.Books).SourceId);
        var unfiltered = await service.SearchAsync(new BookSearchRequest { Query = "x" }, default);
        Assert.Equal(5, unfiltered.Books.Count);
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData("asc", "2", "1", "3")]
    [InlineData("desc", "1", "2", "3")]
    public async Task YearSortingAlwaysPlacesUnknownYearsLast(string direction, string first, string second, string third)
    {
        using var cache = TestData.Cache();
        var service = new BookAggregationService([StubProvider.Returning("A",
            TestData.Book("1", year: 2000), TestData.Book("2", year: 1800), TestData.Book("3", year: null))], cache);
        var result = await service.SearchAsync(new BookSearchRequest
        { Query = "x", SortBy = "publishedYear", SortOrder = direction }, default);
        Assert.Equal([first, second, third], result.Books.Select(book => book.SourceId));
    }

    [Theory]
    [InlineData("title", "asc", "2", "1")]
    [InlineData("TITLE", "DESC", "1", "2")]
    [InlineData("source", "asc", "1", "2")]
    [InlineData("source", "desc", "2", "1")]
    public async Task TextSortingHasStableCaseInsensitiveOrder(string sort, string direction, string first, string second)
    {
        using var cache = TestData.Cache();
        var service = new BookAggregationService([StubProvider.Returning("A",
            TestData.Book("1", title: "zebra", source: "A"), TestData.Book("2", title: "Alpha", source: "B"))], cache);
        var result = await service.SearchAsync(new BookSearchRequest
        { Query = "x", SortBy = sort, SortOrder = direction }, default);
        Assert.Equal([first, second], result.Books.Select(book => book.SourceId));
    }

    [Fact]
    public async Task CallerCancellationReachesEveryActiveProviderAndIsNotFallback()
    {
        var started = TestData.Signal();
        var count = 0;
        var cancelled = 0;
        var providers = Enumerable.Range(1, 3).Select(i => new StubProvider($"P{i}", async (_, token) =>
        {
            if (Interlocked.Increment(ref count) == 3) started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return [];
        })).ToArray();
        using var cache = TestData.Cache();
        using var cts = new CancellationTokenSource();
        var task = new BookAggregationService(providers, cache).SearchAsync(new BookSearchRequest { Query = "x" }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(3, cancelled);
    }

    [Fact]
    public async Task ProgrammingErrorsAreNotHiddenAsProviderOutages()
    {
        using var cache = TestData.Cache();
        var broken = new StubProvider("A", (_, _) => throw new InvalidOperationException("bug"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BookAggregationService([broken], cache)
            .SearchAsync("x", default));
    }
}
