using System.ComponentModel.DataAnnotations;

namespace BookAggregator.Api.Configuration;

public sealed class ProviderHttpOptions
{
    public const string SectionName = "ProviderHttp";

    [Range(1, 30)]
    public int AttemptTimeoutSeconds { get; init; } = 3;

    [Range(0, 2)]
    public int MaxRetries { get; init; } = 1;

    [Range(1, 1000)]
    public int RetryDelayMilliseconds { get; init; } = 200;

    [Range(1, 5000)]
    public int MaxRetryDelayMilliseconds { get; init; } = 1000;
}
