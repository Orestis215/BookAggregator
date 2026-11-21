
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookAggregator.Api.Models;

namespace BookAggregator.Api.Providers;

public sealed class OpenLibraryProvider : IBookProvider
{
    private readonly HttpClient _httpClient;

    public string Name => "OpenLibrary";

    public OpenLibraryProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<BookDto>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encodedQuery = Uri.EscapeDataString(query);

        var url =
            $"search.json?q={encodedQuery}&limit=10" +
            "&fields=key,title,author_name,first_publish_year,subject";

        var response = await _httpClient
            .GetFromJsonAsync<OpenLibraryResponse>(
                url,
                cancellationToken);

        if (response?.Docs is null)
        {
            throw new JsonException("Open Library returned an invalid search response.");
        }

        return response.Docs
            .Where(book =>
                book is not null && !string.IsNullOrWhiteSpace(book.Key) &&
                !string.IsNullOrWhiteSpace(book.Title))
            .Select(book => new BookDto
            {
                Source = Name,
                SourceId = book!.Key!,
                Title = book.Title!,
                Authors = book.AuthorNames?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [],
                PublishedYear = book.FirstPublishYear,
                Categories = book.Subjects?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [],
                Url = book.Key!.StartsWith("/works/", StringComparison.Ordinal)
                    ? $"https://openlibrary.org{book.Key}"
                    : $"https://openlibrary.org/works/{Uri.EscapeDataString(book.Key)}"
            })
            .ToList();
    }

    private sealed class OpenLibraryResponse
    {
        [JsonPropertyName("docs")]
        public List<OpenLibraryBook?>? Docs { get; init; }
    }

    private sealed class OpenLibraryBook
    {
        [JsonPropertyName("key")]
        public string? Key { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("author_name")]
        public List<string>? AuthorNames { get; init; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; init; }

        [JsonPropertyName("subject")]
        public List<string>? Subjects { get; init; }
    }
}
