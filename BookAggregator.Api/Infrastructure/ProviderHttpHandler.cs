using System.Diagnostics;
using System.Net;
using BookAggregator.Api.Configuration;
using BookAggregator.Api.Services;

namespace BookAggregator.Api.Infrastructure;

// Used only by the providers' idempotent GET requests. No retry of deserialization failures.
public sealed class ProviderHttpHandler(
    string source,
    ProviderStatistics statistics,
    ProviderHttpOptions options,
    TimeProvider timeProvider) : DelegatingHandler
{
    public const long MaxResponseBytes = 2 * 1024 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get)
            throw new InvalidOperationException("Provider HTTP policies support GET requests only.");

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan retryDelay;
            try
            {
                var response = await SendAttemptAsync(request, cancellationToken);
                retryDelay = GetRetryDelay(response, attempt);
                if (attempt >= options.MaxRetries || !IsTransient(response.StatusCode) ||
                    retryDelay > TimeSpan.FromMilliseconds(options.MaxRetryDelayMilliseconds))
                    return response;

                response.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException &&
                                       !cancellationToken.IsCancellationRequested && attempt < options.MaxRetries)
            {
                retryDelay = GetRetryDelay(null, attempt);
            }

            await Task.Delay(retryDelay, timeProvider, cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendAttemptAsync(
        HttpRequestMessage original, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(options.AttemptTimeoutSeconds), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy
        };
        foreach (var header in original.Headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        var started = Stopwatch.GetTimestamp();
        var success = false;
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, linked.Token);
            // Include full network body transfer in the timeout AND attempt statistics.
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, linked.Token);
            success = response.IsSuccessStatusCode;
            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            response?.Dispose();
            throw new TimeoutException($"{source} exceeded the per-attempt timeout.", ex);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
        finally
        {
            statistics.Record(source, Stopwatch.GetElapsedTime(started), success,
                cancellationToken.IsCancellationRequested);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retryAfter?.Date is { } date)
        {
            var delay = date - timeProvider.GetUtcNow();
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        var milliseconds = options.RetryDelayMilliseconds * Math.Pow(2, attempt) + Random.Shared.Next(0, 50);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, options.MaxRetryDelayMilliseconds));
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
