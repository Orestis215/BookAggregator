using BookAggregator.Api.Models;
using BookAggregator.Api.Providers;

namespace BookAggregator.Api.Services;

public sealed class BookAggregationService
{
    private readonly IReadOnlyList<IBookProvider> _providers;
    private readonly ProviderSearchCache _cache;

    public IReadOnlyList<string> ProviderNames => _providers.Select(provider => provider.Name).ToArray();

    public BookAggregationService(
        IEnumerable<IBookProvider> providers, ProviderSearchCache cache)
    {
        _providers = providers.ToArray();
        _cache = cache;
    }

    public async Task<IReadOnlyList<BookDto>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var result = await SearchAsync(new BookSearchRequest { Query = query }, cancellationToken);
        return result.Books;
    }

    public async Task<BookSearchResult> SearchAsync(
        BookSearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = _providers.Where(provider => request.Source is null ||
            provider.Name.Equals(request.Source.Trim(), StringComparison.OrdinalIgnoreCase));
        var tasks = selected.Select(provider => _cache.SearchAsync(provider, request.Query, cancellationToken));

        var results = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<BookDto> books = results.SelectMany(result => result.Books);

        if (request.Author is not null)
            books = books.Where(book => book.Authors.Any(author =>
                author.Contains(request.Author.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (request.Category is not null)
            books = books.Where(book => book.Categories.Any(category =>
                category.Contains(request.Category.Trim(), StringComparison.OrdinalIgnoreCase)));
        if (request.MinYear is not null)
            books = books.Where(book => book.PublishedYear >= request.MinYear);
        if (request.MaxYear is not null)
            books = books.Where(book => book.PublishedYear <= request.MaxYear);

        var descending = request.SortOrder.Equals("desc", StringComparison.OrdinalIgnoreCase);
        var sort = request.SortBy.ToLowerInvariant();
        if (sort != "relevance")
        {
            IOrderedEnumerable<BookDto> ordered;
            if (sort == "publishedyear")
            {
                // Unknown years are last in BOTH directions.
                var knownFirst = books.OrderBy(book => book.PublishedYear is null);
                ordered = descending
                    ? knownFirst.ThenByDescending(book => book.PublishedYear)
                    : knownFirst.ThenBy(book => book.PublishedYear);
            }
            else
            {
                Func<BookDto, string> key = sort == "source" ? book => book.Source : book => book.Title;
                ordered = descending
                    ? books.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)
                    : books.OrderBy(key, StringComparer.OrdinalIgnoreCase);
            }

            books = ordered.ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(book => book.Source, StringComparer.Ordinal)
                .ThenBy(book => book.SourceId, StringComparer.Ordinal);
        }

        return new BookSearchResult(books.ToArray(),
            results.Where(result => result.IsUnavailable).Select(result => result.Source).ToArray(),
            results.Where(result => result.IsStale).Select(result => result.Source).ToArray(),
            results.Any(result => !result.IsUnavailable || result.IsStale));
    }
}
