using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using BookAggregator.Api.Models;

namespace BookAggregator.Api.Providers;

public sealed class AppleBooksProvider(HttpClient httpClient) : IBookProvider
{
    public string Name => "AppleBooks";

    public async Task<IReadOnlyList<BookDto>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await httpClient.GetFromJsonAsync<AppleResponse>(
            $"search?term={Uri.EscapeDataString(query)}&media=ebook&entity=ebook&country=US&limit=10",
            cancellationToken);
        if (response?.Results is null)
            throw new JsonException("Apple Books returned an invalid search response.");

        return response.Results
            .Where(book => book is not null && book.TrackId > 0 && !string.IsNullOrWhiteSpace(book.TrackName))
            .Select(book => new BookDto
            {
                Source = Name,
                SourceId = book!.TrackId.ToString(CultureInfo.InvariantCulture),
                Title = book.TrackName!,
                Authors = string.IsNullOrWhiteSpace(book.ArtistName) ? [] : [book.ArtistName],
                PublishedYear = DateTimeOffset.TryParse(book.ReleaseDate, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) ? date.Year : null,
                Categories = book.Genres?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [],
                Url = book.TrackViewUrl
            }).ToArray();
    }

    private sealed class AppleResponse
    {
        public List<AppleBook?>? Results { get; init; }
    }

    private sealed class AppleBook
    {
        public long TrackId { get; init; }
        public string? TrackName { get; init; }
        public string? ArtistName { get; init; }
        public string? ReleaseDate { get; init; }
        public List<string>? Genres { get; init; }
        public string? TrackViewUrl { get; init; }
    }
}
