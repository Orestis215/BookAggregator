using BookAggregator.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BookAggregator.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("BookCache:FreshSeconds", "0")]
    [InlineData("BookCache:StaleSeconds", "100")]
    [InlineData("BookCache:FailureBackoffSeconds", "0")]
    [InlineData("BookCache:MaxEntries", "0")]
    [InlineData("ProviderHttp:AttemptTimeoutSeconds", "0")]
    [InlineData("ProviderHttp:MaxRetries", "3")]
    [InlineData("ProviderHttp:MaxRetryDelayMilliseconds", "100")]
    public void InvalidPolicySettingsAreRejected(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();
        using var services = new ServiceCollection().AddLogging().AddBookAggregation(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
        {
            if (key.StartsWith("BookCache", StringComparison.Ordinal))
                _ = services.GetRequiredService<IOptions<BookCacheOptions>>().Value;
            else
                _ = services.GetRequiredService<IOptions<ProviderHttpOptions>>().Value;
        });
    }
}
