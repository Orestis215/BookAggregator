using System.Net;
using System.Net.Http.Headers;
using BookAggregator.Api.Configuration;
using BookAggregator.Api.Infrastructure;
using BookAggregator.Api.Services;

namespace BookAggregator.Tests;

public sealed class HttpPolicyTests
{
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TransientResponsesRetryAndEveryAttemptIsCounted(int status)
    {
        var calls = 0;
        var statistics = new ProviderStatistics();
        using var client = Client((request, _) =>
        {
            Assert.Equal("BookAggregatorTest/1.0", request.Headers.UserAgent.ToString());
            var response = StubHttpHandler.Json("{}", ++calls == 1 ? (HttpStatusCode)status : HttpStatusCode.OK);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }, statistics);
        using var response = await client.GetAsync("search");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
        var snapshot = Assert.Single(statistics.Snapshot(["A"]));
        Assert.Equal(2, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.SuccessfulRequests);
        Assert.Equal(1, snapshot.FailedRequests);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task PermanentErrorsAreNotRetried(int status)
    {
        var calls = 0;
        using var client = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(StubHttpHandler.Json("{}", (HttpStatusCode)status));
        });
        using var response = await client.GetAsync("search");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetryLimitIsEnforcedForNetworkFailures()
    {
        var calls = 0;
        var statistics = new ProviderStatistics();
        using var client = Client((_, _) =>
        {
            calls++;
            throw new HttpRequestException("network unavailable");
        }, statistics);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("search"));
        Assert.Equal(2, calls);
        Assert.Equal(2, Assert.Single(statistics.Snapshot(["A"])).FailedRequests);
    }

    [Fact]
    public async Task RetryLimitAlsoReturnsFinalTransientHttpResponse()
    {
        var calls = 0;
        using var client = Client((_, _) =>
        {
            calls++;
            return Task.FromResult(StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
        });
        using var response = await client.GetAsync("search");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongRetryAfterIsNotRetriedEarly(bool useDate)
    {
        var calls = 0;
        using var client = Client((_, _) =>
        {
            calls++;
            var response = StubHttpHandler.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = useDate
                ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(1))
                : new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        });
        using var result = await client.GetAsync("search");
        Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AttemptTimeoutIsRetriedAndEventuallySurfaces()
    {
        var statistics = new ProviderStatistics();
        var calls = 0;
        using var client = Client(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return StubHttpHandler.Json("{}");
        }, statistics, new ProviderHttpOptions { AttemptTimeoutSeconds = 1, RetryDelayMilliseconds = 1 });
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync("search"));
        Assert.Equal(2, calls);
        var snapshot = Assert.Single(statistics.Snapshot(["A"]));
        Assert.Equal(2, snapshot.FailedRequests);
        Assert.Equal(0, snapshot.CancelledRequests);
    }

    [Fact]
    public async Task AttemptTimeoutIncludesSlowResponseBody()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new SlowContent() }), options: new ProviderHttpOptions { AttemptTimeoutSeconds = 1, MaxRetries = 0 });
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetAsync("search"));
    }

    [Fact]
    public async Task CallerCancellationIsNotRetriedAndIsCountedSeparately()
    {
        var entered = TestData.Signal();
        var calls = 0;
        var statistics = new ProviderStatistics();
        using var client = Client(async (_, token) =>
        {
            calls++;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return StubHttpHandler.Json("{}");
        }, statistics);
        using var cts = new CancellationTokenSource();
        var task = client.GetAsync("search", cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, calls);
        var snapshot = Assert.Single(statistics.Snapshot(["A"]));
        Assert.Equal(1, snapshot.CancelledRequests);
        Assert.Equal(0, snapshot.FailedRequests);
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsNextAttempt()
    {
        var responded = TestData.Signal();
        var calls = 0;
        using var client = Client((_, _) =>
        {
            calls++;
            var response = StubHttpHandler.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            responded.TrySetResult();
            return Task.FromResult(response);
        });
        using var cts = new CancellationTokenSource();
        var task = client.GetAsync("search", cts.Token);
        await responded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OversizedBodiesAreRejected()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[ProviderHttpHandler.MaxResponseBytes + 1]) }),
            options: new ProviderHttpOptions { MaxRetries = 0 });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("search"));
    }

    private static HttpClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        ProviderStatistics? statistics = null, ProviderHttpOptions? options = null)
    {
        var handler = new ProviderHttpHandler("A", statistics ?? new ProviderStatistics(),
            options ?? new ProviderHttpOptions { RetryDelayMilliseconds = 1 }, TimeProvider.System)
        { InnerHandler = new StubHttpHandler(respond) };
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://test/"), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BookAggregatorTest/1.0");
        return client;
    }

    private sealed class SlowContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Cancellation-aware overload is required.");
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
