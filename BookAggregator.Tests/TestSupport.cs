using System.Net;
using BookAggregator.Api.Configuration;
using BookAggregator.Api.Models;
using BookAggregator.Api.Providers;
using BookAggregator.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BookAggregator.Tests;

internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

internal sealed class StubProvider(string name, Func<string, CancellationToken, Task<IReadOnlyList<BookDto>>> search) : IBookProvider
{
    private int _calls;
    public string Name => name;
    public int Calls => Volatile.Read(ref _calls);
    public Task<IReadOnlyList<BookDto>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return search(query, cancellationToken);
    }

    public static StubProvider Returning(string name, params BookDto[] books) =>
        new(name, (_, _) => Task.FromResult<IReadOnlyList<BookDto>>(books));

    public static StubProvider Failing(string name) =>
        new(name, (_, _) => throw new HttpRequestException("test outage"));
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan interval) => _now += interval;
}

internal static class TestData
{
    public static BookDto Book(string id = "1", string title = "Frankenstein", string source = "A",
        int? year = 1818, string[]? authors = null, string[]? categories = null) => new()
        {
            Source = source,
            SourceId = id,
            Title = title,
            PublishedYear = year,
            Authors = authors ?? ["Mary Shelley"],
            Categories = categories ?? ["Fiction"]
        };

    public static ProviderSearchCache Cache(TimeProvider? clock = null, BookCacheOptions? options = null) =>
        new(Options.Create(options ?? new BookCacheOptions()), clock ?? TimeProvider.System,
            NullLogger<ProviderSearchCache>.Instance);

    public static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
