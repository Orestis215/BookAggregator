
using System.Net.Http.Json;
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
        var encodedQuery = Uri.EscapeDataString(query);

        var url =
            $"search.json?q={encodedQuery}&limit=10" +
            "&fields=key,title,author_name,first_publish_year,subject";

        var response = await _httpClient
            .GetFromJsonAsync<OpenLibraryResponse>(
                url,
                cancellationToken);

        if (response is null)
        {
            throw new InvalidOperationException(
                "Open Library returned an empty response.");
        }

        return response.Docs
            .Where(book =>
                !string.IsNullOrWhiteSpace(book.Key) &&
                !string.IsNullOrWhiteSpace(book.Title))
            .Select(book => new BookDto
            {
                Source = Name,
                SourceId = book.Key!,
                Title = book.Title!,
                Authors = book.AuthorNames ?? [],
                PublishedYear = book.FirstPublishYear,
                Categories = book.Subjects ?? [],
                Url = $"https://openlibrary.org{book.Key}"
            })
            .ToList();
    }

    private sealed class OpenLibraryResponse
    {
        [JsonPropertyName("docs")]
        public List<OpenLibraryBook> Docs { get; init; } = [];
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
