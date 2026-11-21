namespace BookAggregator.Api.Models;

public sealed class BookDto
{
    public required string Source { get; init; }

    public required string SourceId { get; init; }

    public required string Title { get; init; }

    public IReadOnlyList<string> Authors { get; init; } = [];

    public int? PublishedYear { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public string? Url { get; init; }
}