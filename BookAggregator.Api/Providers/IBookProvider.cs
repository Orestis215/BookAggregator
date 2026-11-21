using BookAggregator.Api.Models;

namespace BookAggregator.Api.Providers;

public interface IBookProvider
{
    string Name { get; }

    Task<IReadOnlyList<BookDto>> SearchAsync(
        string query,
        CancellationToken cancellationToken);
}