using System.ComponentModel.DataAnnotations;

namespace BookAggregator.Api.Configuration;

public sealed class BookCacheOptions
{
    public const string SectionName = "BookCache";

    [Range(1, 3600)]
    public int FreshSeconds { get; init; } = 120;

    // Total lifetime from the last successful fetch, including the fresh period.
    [Range(1, 86400)]
    public int StaleSeconds { get; init; } = 900;

    [Range(1, 300)]
    public int FailureBackoffSeconds { get; init; } = 10;

    [Range(1, 10000)]
    public int MaxEntries { get; init; } = 1000;
}
