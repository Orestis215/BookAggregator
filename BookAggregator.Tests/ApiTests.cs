using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookAggregator.Api.Models;
using BookAggregator.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;

namespace BookAggregator.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task StatisticsEndpointReturnsMeanDurationForEachBucket()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var statistics = factory.Services.GetRequiredService<ProviderStatistics>();
        foreach (var elapsedMs in new[] { 25, 100, 600, 800, 2000, 3500 })
            statistics.Record("OpenLibrary", TimeSpan.FromMilliseconds(elapsedMs), true);

        var rows = await client.GetFromJsonAsync<JsonElement>("/api/statistics");
        var row = Assert.Single(rows.EnumerateArray(), item => item.GetProperty("source").GetString() == "OpenLibrary");
        Assert.Equal(62.5, row.GetProperty("fastMeanResponseTimeMs").GetDouble());
        Assert.Equal(700, row.GetProperty("averageMeanResponseTimeMs").GetDouble());
        Assert.Equal(2750, row.GetProperty("slowMeanResponseTimeMs").GetDouble());
    }

    [Fact]
    public async Task RealPipelineAggregatesThreeProvidersAndCacheHitsDoNotIncreaseHttpStatistics()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var before = await client.GetFromJsonAsync<ProviderStatisticsDto[]>("/api/statistics");
        Assert.Equal(3, before!.Length);
        Assert.All(before, row => Assert.Equal(0, row.TotalRequests));

        using var first = await client.GetAsync("/api/books?query=frankenstein&sortBy=title");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var books = await first.Content.ReadFromJsonAsync<BookDto[]>();
        Assert.Equal(3, books!.Length);
        Assert.Equal(3, books.Select(book => book.Source).Distinct().Count());
        Assert.False(first.Headers.Contains("X-Unavailable-Providers"));
        Assert.True(first.Headers.CacheControl!.NoStore);

        using var second = await client.GetAsync("/api/books?query=%20frankenstein%20&sortBy=publishedYear&sortOrder=desc");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var stats = await client.GetFromJsonAsync<ProviderStatisticsDto[]>("/api/statistics");
        Assert.All(stats!, row =>
        {
            Assert.Equal(1, row.TotalRequests);
            Assert.Equal(1, row.SuccessfulRequests);
        });
    }

    [Theory]
    [InlineData(503)]
    [InlineData(200)]
    public async Task OutageOrInvalidJsonKeepsHealthyProviderResults(int status)
    {
        await using var factory = new TestApiFactory((request, _) => Task.FromResult(
            request.RequestUri!.Host == "itunes.apple.com"
                ? StubHttpHandler.Json("<html>failure</html>", (HttpStatusCode)status)
                : TestApiFactory.HealthyResponse(request)));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/books?query=x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AppleBooks", Assert.Single(response.Headers.GetValues("X-Unavailable-Providers")));
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<BookDto[]>())!.Length);
        var stats = await client.GetFromJsonAsync<ProviderStatisticsDto[]>("/api/statistics");
        var apple = Assert.Single(stats!, row => row.Source == "AppleBooks");
        Assert.Equal(status == 503 ? 2 : 1, apple.TotalRequests); // no retry for bad JSON
    }

    [Fact]
    public async Task AllProvidersUnavailableReturns503ProblemDetails()
    {
        await using var factory = new TestApiFactory((_, _) => Task.FromResult(
            StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable)));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/books?query=x");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(503, body.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task SuccessfulEmptySearchReturns200EmptyArray()
    {
        await using var factory = new TestApiFactory((request, _) => Task.FromResult(StubHttpHandler.Json(
            request.RequestUri!.Host switch
            {
                "openlibrary.org" => "{\"docs\":[]}",
                "itunes.apple.com" => "{\"results\":[]}",
                _ => "{\"status\":\"not found\"}"
            })));
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/books?query=x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<BookDto[]>())!);
        Assert.False(response.Headers.Contains("X-Unavailable-Providers"));
    }

    [Fact]
    public async Task SourceFilterCallsOnlyRequestedProvider()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var books = await client.GetFromJsonAsync<BookDto[]>("/api/books?query=x&source=applebooks");
        Assert.Equal("AppleBooks", Assert.Single(books!).Source);
        var stats = await client.GetFromJsonAsync<ProviderStatisticsDto[]>("/api/statistics");
        Assert.All(stats!, row => Assert.Equal(row.Source == "AppleBooks" ? 1 : 0, row.TotalRequests));
    }

    [Fact]
    public async Task RealPipelineAppliesCombinedFiltersAndYearSorting()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        var books = await client.GetFromJsonAsync<BookDto[]>(
            "/api/books?query=x&author=SHELLEY&category=FICTION&minYear=1800&maxYear=1900&sortBy=publishedYear&sortOrder=desc");
        Assert.Equal([1818, 1817], books!.Select(book => book.PublishedYear));
        Assert.DoesNotContain(books!, book => book.Source == "DBooks");
    }

    [Fact]
    public async Task StaleFallbackReturns200WithHeadersUntilOriginalDeadlineThen503()
    {
        var clock = new ManualClock();
        var healthy = true;
        await using var factory = new TestApiFactory((request, _) => Task.FromResult(healthy
            ? TestApiFactory.HealthyResponse(request)
            : StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable)), services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
        using var client = factory.CreateClient();
        const string path = "/api/books?query=x&source=OpenLibrary";
        using var initial = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        healthy = false;
        clock.Advance(TimeSpan.FromSeconds(120));
        using var fallback = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, fallback.StatusCode);
        Assert.Single((await fallback.Content.ReadFromJsonAsync<BookDto[]>())!);
        Assert.Equal("OpenLibrary", Assert.Single(fallback.Headers.GetValues("X-Stale-Providers")));
        Assert.Equal("OpenLibrary", Assert.Single(fallback.Headers.GetValues("X-Unavailable-Providers")));
        var beforeBackoff = await client.GetFromJsonAsync<ProviderStatisticsDto[]>("/api/statistics");
        using var backoff = await client.GetAsync(path);
        var afterBackoff = await client.GetFromJsonAsync<ProviderStatisticsDto[]>("/api/statistics");
        Assert.Equal(beforeBackoff, afterBackoff);
        clock.Advance(TimeSpan.FromSeconds(780));
        using var expired = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, expired.StatusCode);
        Assert.False(expired.Headers.Contains("X-Stale-Providers"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?query=")]
    [InlineData("?query=%20%20")]
    [InlineData("?query=x&sortBy=bad")]
    [InlineData("?query=x&sortBy=")]
    [InlineData("?query=x&sortOrder=sideways")]
    [InlineData("?query=x&source=Unknown")]
    [InlineData("?query=x&source=%20%20")]
    [InlineData("?query=x&source=")]
    [InlineData("?query=x&author=%20%20")]
    [InlineData("?query=x&author=")]
    [InlineData("?query=x&category=%20%20")]
    [InlineData("?query=x&category=")]
    [InlineData("?query=x&minYear=2020&maxYear=1900")]
    [InlineData("?query=x&minYear=0")]
    [InlineData("?query=x&maxYear=10000")]
    [InlineData("?query=x&minYear=abc")]
    public async Task InvalidInputReturns400WithoutExternalCalls(string query)
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/books" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var stats = await client.GetFromJsonAsync<ProviderStatisticsDto[]>("/api/statistics");
        Assert.All(stats!, row => Assert.Equal(0, row.TotalRequests));
    }

    [Fact]
    public async Task ExcessiveQueryLengthIsRejected()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/books?query=" + new string('x', 201));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentApiRequestsCoalesceExternalCalls()
    {
        await using var factory = new TestApiFactory(async (request, token) =>
        {
            await Task.Delay(40, token);
            return TestApiFactory.HealthyResponse(request);
        });
        using var client = factory.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => client.GetAsync("/api/books?query=x")));
        foreach (var response in responses)
        {
            using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var stats = await client.GetFromJsonAsync<ProviderStatisticsDto[]>("/api/statistics");
        Assert.All(stats!, row => Assert.Equal(1, row.TotalRequests));
    }

    [Fact]
    public async Task OpenApiDocumentDescribesBothEndpointsAndParameters()
    {
        await using var factory = new TestApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/books", out var books));
        Assert.True(paths.TryGetProperty("/api/statistics", out _));
        var parameters = books.GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString()).ToArray();
        Assert.Contains("Query", parameters);
        Assert.Contains("SortBy", parameters);
    }
}

internal sealed class TestApiFactory(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
    Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler =
                    new StubHttpHandler(send ?? ((request, _) => Task.FromResult(HealthyResponse(request))))));
            configure?.Invoke(services);
        });
    }

    public static HttpResponseMessage HealthyResponse(HttpRequestMessage request) =>
        StubHttpHandler.Json(request.RequestUri!.Host switch
        {
            "openlibrary.org" => """{"docs":[{"key":"/works/OL1W","title":"Frankenstein","author_name":["Mary Shelley"],"first_publish_year":1818,"subject":["Fiction"]}]}""",
            "itunes.apple.com" => """{"results":[{"trackId":123,"trackName":"Frankenstein","artistName":"Mary Shelley","releaseDate":"1817-12-31T00:00:00Z","genres":["Fiction"]}]}""",
            "www.dbooks.org" => """{"status":"ok","books":[{"id":"456","title":"Python","authors":"Alice"}]}""",
            _ => throw new InvalidOperationException("Unexpected provider host.")
        });
}
