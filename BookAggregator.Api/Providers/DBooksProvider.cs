using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookAggregator.Api.Models;

namespace BookAggregator.Api.Providers;

public sealed class DBooksProvider(HttpClient httpClient) : IBookProvider
{
    public string Name => "DBooks";

    public async Task<IReadOnlyList<BookDto>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await httpClient.GetFromJsonAsync<DBooksResponse>(
            $"api/search/{Uri.EscapeDataString(query)}", cancellationToken);

        // This is dBooks' documented/live no-results response, not a service outage.
        if (response?.Status == "not found") return [];
        if (response?.Status != "ok" || response.Books is null)
            throw new JsonException("dBooks returned an invalid search response.");

        return response.Books
            .Where(book => book is not null && !string.IsNullOrWhiteSpace(book.Id) &&
                           !string.IsNullOrWhiteSpace(book.Title))
            .Select(book => new BookDto
            {
                Source = Name,
                SourceId = book!.Id!,
                Title = WebUtility.HtmlDecode(book.Title!),
                Authors = string.IsNullOrWhiteSpace(book.Authors) ? [] :
                    WebUtility.HtmlDecode(book.Authors).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                // Search does not include year/categories. Avoid a details HTTP call per book.
                Url = book.Url
            }).ToArray();
    }

    private sealed class DBooksResponse
    {
        public string? Status { get; init; }
        public List<DBook?>? Books { get; init; }
    }

    private sealed class DBook
    {
        public string? Id { get; init; }
        public string? Title { get; init; }
        public string? Authors { get; init; }
        public string? Url { get; init; }
    }
}
