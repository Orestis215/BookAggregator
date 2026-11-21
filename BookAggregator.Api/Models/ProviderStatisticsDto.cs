namespace BookAggregator.Api.Models;

public sealed record ProviderStatisticsDto(
    string Source,
    long TotalRequests,
    long SuccessfulRequests,
    long FailedRequests,
    long CancelledRequests,
    double AverageResponseTimeMs,
    long FastRequests,
    long AverageRequests,
    long SlowRequests,
    double FastMeanResponseTimeMs,
    double AverageMeanResponseTimeMs,
    double SlowMeanResponseTimeMs);
