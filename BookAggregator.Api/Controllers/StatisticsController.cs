using BookAggregator.Api.Models;
using BookAggregator.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BookAggregator.Api.Controllers;

[ApiController]
[Route("api/statistics")]
public sealed class StatisticsController(ProviderStatistics statistics, BookAggregationService aggregationService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProviderStatisticsDto>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ProviderStatisticsDto>> GetStatistics()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(statistics.Snapshot(aggregationService.ProviderNames));
    }
}
