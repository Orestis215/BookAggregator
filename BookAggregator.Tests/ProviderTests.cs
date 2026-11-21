using System.Net;
using System.Text.Json;
using BookAggregator.Api.Providers;
using Microsoft.AspNetCore.WebUtilities;

namespace BookAggregator.Tests;

public sealed class ProviderTests
{
    [Fact]
    public async Task OpenLibraryMapsFieldsAndEscapesQuery()
    {
        using var client = Client(request =>
        {
            Assert.Equal("/search.json", request.RequestUri!.AbsolutePath);
            Assert.Equal("Mary & science/τέχνη?", QueryHelpers.ParseQuery(request.RequestUri.Query)["q"]);
            Assert.Equal("10", QueryHelpers.ParseQuery(request.RequestUri.Query)["limit"]);
            return """{"docs":[null,{"title":"No key"},{"key":"/works/OL1W","title":"Frankenstein","author_name":["Mary Shelley"],"first_publish_year":1818,"subject":["Fiction"]}]}""";
        });
        var book = Assert.Single(await new OpenLibraryProvider(client).SearchAsync("Mary & science/τέχνη?", default));
        Assert.Equal("OpenLibrary", book.Source);
        Assert.Equal("/works/OL1W", book.SourceId);
        Assert.Equal("Frankenstein", book.Title);
        Assert.Equal(["Mary Shelley"], book.Authors);
        Assert.Equal(1818, book.PublishedYear);
        Assert.Equal(["Fiction"], book.Categories);
        Assert.Equal("https://openlibrary.org/works/OL1W", book.Url);
    }

    [Fact]
    public async Task OpenLibraryAcceptsBareWorkIdsAndMissingOptionalFields()
    {
        using var client = Client(_ => """{"docs":[{"key":"OL1W","title":"Title"}]}""");
        var book = Assert.Single(await new OpenLibraryProvider(client).SearchAsync("x", default));
        Assert.Empty(book.Authors);
        Assert.Empty(book.Categories);
        Assert.Null(book.PublishedYear);
        Assert.Equal("https://openlibrary.org/works/OL1W", book.Url);
    }

    [Fact]
    public async Task AppleBooksMapsFieldsAndRequestsEbooksOnly()
    {
        using var client = Client(request =>
        {
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            Assert.Equal("Mary & Shelley", query["term"]);
            Assert.Equal("ebook", query["media"]);
            Assert.Equal("ebook", query["entity"]);
            Assert.Equal("US", query["country"]);
            return """{"results":[null,{"trackId":0,"trackName":"bad"},{"trackId":1437082867,"trackName":"Frankenstein","artistName":"Mary Shelley","releaseDate":"1817-12-31T08:00:00Z","genres":["Fiction"],"trackViewUrl":"https://books.apple.com/book/1"}]}""";
        });
        var book = Assert.Single(await new AppleBooksProvider(client).SearchAsync("Mary & Shelley", default));
        Assert.Equal("AppleBooks", book.Source);
        Assert.Equal("1437082867", book.SourceId);
        Assert.Equal("Frankenstein", book.Title);
        Assert.Equal(["Mary Shelley"], book.Authors);
        Assert.Equal(1817, book.PublishedYear);
        Assert.Equal(["Fiction"], book.Categories);
        Assert.Equal("https://books.apple.com/book/1", book.Url);
    }

    [Fact]
    public async Task AppleBooksAllowsMissingMetadataAndInvalidDate()
    {
        using var client = Client(_ => """{"results":[{"trackId":1,"trackName":"Title","releaseDate":"bad"}]}""");
        var book = Assert.Single(await new AppleBooksProvider(client).SearchAsync("x", default));
        Assert.Empty(book.Authors);
        Assert.Empty(book.Categories);
        Assert.Null(book.PublishedYear);
    }

    [Fact]
    public async Task DBooksMapsSearchWithoutInventingYearOrIssuingDetailCalls()
    {
        var calls = 0;
        using var client = Client(request =>
        {
            calls++;
            Assert.Equal("/api/search/C%23%20%26%20.NET%2F10", request.RequestUri!.AbsolutePath);
            return """{"status":"ok","books":[null,{"id":"","title":"bad"},{"id":"123","title":"Science &amp; Code","authors":"Alice, Bob O&#039;Connor","url":"https://www.dbooks.org/book-123/"}]}""";
        });
        var book = Assert.Single(await new DBooksProvider(client).SearchAsync("C# & .NET/10", default));
        Assert.Equal(1, calls);
        Assert.Equal("DBooks", book.Source);
        Assert.Equal("123", book.SourceId);
        Assert.Equal("Science & Code", book.Title);
        Assert.Equal(["Alice", "Bob O'Connor"], book.Authors);
        Assert.Null(book.PublishedYear);
        Assert.Empty(book.Categories);
        Assert.Equal("https://www.dbooks.org/book-123/", book.Url);
    }

    [Theory]
    [InlineData("OpenLibrary", "{\"docs\":[]}")]
    [InlineData("AppleBooks", "{\"results\":[]}")]
    [InlineData("DBooks", "{\"status\":\"not found\"}")]
    [InlineData("DBooks", "{\"status\":\"ok\",\"books\":[]}")]
    public async Task ValidEmptySearchIsNotAnOutage(string source, string json)
    {
        using var client = Client(_ => json);
        Assert.Empty(await Provider(source, client).SearchAsync("x", default));
    }

    [Theory]
    [InlineData("OpenLibrary", "{}")]
    [InlineData("AppleBooks", "{\"results\":null}")]
    [InlineData("DBooks", "{\"status\":\"error\"}")]
    [InlineData("DBooks", "{\"status\":\"ok\"}")]
    [InlineData("OpenLibrary", "null")]
    [InlineData("AppleBooks", "<html>error</html>")]
    public async Task InvalidPayloadIsReportedAsFailure(string source, string json)
    {
        using var client = Client(_ => json);
        await Assert.ThrowsAsync<JsonException>(() => Provider(source, client).SearchAsync("x", default));
    }

    [Theory]
    [InlineData("OpenLibrary")]
    [InlineData("AppleBooks")]
    [InlineData("DBooks")]
    public async Task HttpErrorsAndCancellationArePropagated(string source)
    {
        using var client = new HttpClient(new StubHttpHandler((_, _) => Task.FromResult(
            StubHttpHandler.Json("{}", HttpStatusCode.ServiceUnavailable))))
        { BaseAddress = new Uri("https://test/") };
        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(source, client).SearchAsync("x", default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(source, client).SearchAsync("x", cancellation.Token));
    }

    private static HttpClient Client(Func<HttpRequestMessage, string> respond) =>
        new(new StubHttpHandler((request, _) => Task.FromResult(StubHttpHandler.Json(respond(request)))))
        { BaseAddress = new Uri("https://test/") };

    private static IBookProvider Provider(string source, HttpClient client) => source switch
    {
        "OpenLibrary" => new OpenLibraryProvider(client),
        "AppleBooks" => new AppleBooksProvider(client),
        _ => new DBooksProvider(client)
    };
}
