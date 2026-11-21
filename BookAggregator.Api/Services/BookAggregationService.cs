using BookAggregator.Api.Models;
using BookAggregator.Api.Providers;

namespace BookAggregator.Api.Services;

public sealed class BookAggregationService
{
    private readonly IEnumerable<IBookProvider> _providers;

    public BookAggregationService(
        IEnumerable<IBookProvider> providers)
    {
        _providers = providers;
    }

    public async Task<IReadOnlyList<BookDto>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var tasks = _providers.Select(provider =>
            provider.SearchAsync(query, cancellationToken));

        var results = await Task.WhenAll(tasks);

        return results
            .SelectMany(books => books)
            .ToList();
    }
}