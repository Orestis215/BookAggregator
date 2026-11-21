namespace BookAggregator.Api.Models;

public sealed record ProviderSearchResult(
    string Source, IReadOnlyList<BookDto> Books, bool IsUnavailable = false, bool IsStale = false);

public sealed record BookSearchResult(
    IReadOnlyList<BookDto> Books,
    IReadOnlyList<string> UnavailableProviders,
    IReadOnlyList<string> StaleProviders,
    bool HasUsableProvider);
