using System.Text.Json;
using BookAggregator.Api.Configuration;
using BookAggregator.Api.Infrastructure;
using BookAggregator.Api.Models;
using BookAggregator.Api.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BookAggregator.Api.Services;

public sealed class ProviderSearchCache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly AsyncKeyedLock _locks = new();
    private readonly BookCacheOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProviderSearchCache> _logger;

    public ProviderSearchCache(IOptions<BookCacheOptions> options, TimeProvider clock,
        ILogger<ProviderSearchCache> logger)
    {
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        // Dedicated cache: imposing a size limit on ASP.NET's shared cache breaks other consumers.
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _options.MaxEntries });
    }

    public async Task<ProviderSearchResult> SearchAsync(
        IBookProvider provider, string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Trim only: case may affect external search semantics. Filters/sorting are applied later.
        var key = provider.Name + "\n" + query.Trim();
        using var lease = await _locks.AcquireAsync(key, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _cache.TryGetValue<Entry>(key, out var entry);
        var now = _clock.GetUtcNow();
        if (entry is not null && now < entry.NextRefresh)
            return ToResult(provider.Name, entry, now);

        try
        {
            var books = await provider.SearchAsync(query.Trim(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            now = _clock.GetUtcNow();
            var cachedBooks = Array.AsReadOnly(books.ToArray());
            entry = new Entry(cachedBooks, now.AddSeconds(_options.FreshSeconds),
                now.AddSeconds(_options.StaleSeconds), now.AddSeconds(_options.FreshSeconds), false);
            Store(key, entry, _options.StaleSeconds);
            return new ProviderSearchResult(provider.Name, cachedBooks);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is HttpRequestException or TimeoutException or JsonException or OperationCanceledException)
        {
            now = _clock.GetUtcNow();
            _logger.LogWarning("Provider {Provider} search failed ({FailureType}); using fallback when available.",
                provider.Name, ex.GetType().Name);
            // Keep the original stale deadline: repeated failures must never extend stale data's life.
            entry = entry is not null && now < entry.StaleUntil
                ? entry with { NextRefresh = now.AddSeconds(_options.FailureBackoffSeconds), Failed = true }
                : new Entry(null, now, now, now.AddSeconds(_options.FailureBackoffSeconds), true);
            var retention = Math.Max(_options.FailureBackoffSeconds, (entry.StaleUntil - now).TotalSeconds);
            Store(key, entry, retention);
            return ToResult(provider.Name, entry, now);
        }
    }

    private static ProviderSearchResult ToResult(string source, Entry entry, DateTimeOffset now)
    {
        var hasData = entry.Books is not null && now < entry.StaleUntil;
        return new ProviderSearchResult(source, hasData ? entry.Books! : [],
            IsUnavailable: entry.Failed || !hasData, IsStale: hasData && now >= entry.FreshUntil);
    }

    private void Store(string key, Entry entry, double retentionSeconds) =>
        _cache.Set(key, entry, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(retentionSeconds)
        });

    public void Dispose() => _cache.Dispose();

    private sealed record Entry(IReadOnlyList<BookDto>? Books, DateTimeOffset FreshUntil,
        DateTimeOffset StaleUntil, DateTimeOffset NextRefresh, bool Failed);
}
