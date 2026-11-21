using BookAggregator.Api.Infrastructure;
using BookAggregator.Api.Providers;
using BookAggregator.Api.Services;
using Microsoft.Extensions.Options;

namespace BookAggregator.Api.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBookAggregation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProviderHttpOptions>().Bind(configuration.GetSection(ProviderHttpOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.RetryDelayMilliseconds <= options.MaxRetryDelayMilliseconds,
                "RetryDelayMilliseconds must not exceed MaxRetryDelayMilliseconds.")
            .ValidateOnStart();
        services.AddOptions<BookCacheOptions>().Bind(configuration.GetSection(BookCacheOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.StaleSeconds >= options.FreshSeconds,
                "StaleSeconds must be at least FreshSeconds.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ProviderStatistics>();
        services.AddSingleton<ProviderSearchCache>();
        services.AddScoped<BookAggregationService>();
        AddProvider<OpenLibraryProvider>(services, "OpenLibrary", "https://openlibrary.org/");
        AddProvider<AppleBooksProvider>(services, "AppleBooks", "https://itunes.apple.com/");
        AddProvider<DBooksProvider>(services, "DBooks", "https://www.dbooks.org/");
        return services;
    }

    private static void AddProvider<T>(IServiceCollection services, string source, string baseAddress)
        where T : class, IBookProvider
    {
        services.AddHttpClient<T>(client =>
        {
            client.BaseAddress = new Uri(baseAddress);
            // The handler bounds every attempt, including the body; retries are bounded separately.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.MaxResponseContentBufferSize = ProviderHttpHandler.MaxResponseBytes;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BookAggregatorAssessment/1.0");
        }).AddHttpMessageHandler(sp => new ProviderHttpHandler(source,
            sp.GetRequiredService<ProviderStatistics>(),
            sp.GetRequiredService<IOptions<ProviderHttpOptions>>().Value,
            sp.GetRequiredService<TimeProvider>()));
        services.AddTransient<IBookProvider>(sp => sp.GetRequiredService<T>());
    }
}
