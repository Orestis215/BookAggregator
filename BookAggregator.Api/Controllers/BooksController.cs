using BookAggregator.Api.Models;
using BookAggregator.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BookAggregator.Api.Controllers;

[ApiController]
[Route("api/books")]
public sealed class BooksController : ControllerBase
{
    private readonly BookAggregationService _aggregationService;

    public BooksController(
        BookAggregationService aggregationService)
    {
        _aggregationService = aggregationService;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<BookDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<BookDto>>> GetBooks(
        [FromQuery] BookSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Source is not null && !_aggregationService.ProviderNames.Contains(
                request.Source.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(request.Source),
                $"source must be one of: {string.Join(", ", _aggregationService.ProviderNames)}.");
            return ValidationProblem(ModelState);
        }

        var result = await _aggregationService.SearchAsync(request, cancellationToken);

        // Preserve the existing JSON array response; expose degraded results explicitly in headers.
        Response.Headers.CacheControl = "no-store";
        if (result.UnavailableProviders.Count > 0)
            Response.Headers["X-Unavailable-Providers"] = string.Join(",", result.UnavailableProviders);
        if (result.StaleProviders.Count > 0)
            Response.Headers["X-Stale-Providers"] = string.Join(",", result.StaleProviders);

        if (!result.HasUsableProvider)
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No book provider is currently available.",
                detail: "Retry later. No usable cached results are available for this query.");

        return Ok(result.Books);
    }
}
