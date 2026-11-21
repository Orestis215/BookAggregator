using System.ComponentModel.DataAnnotations;

namespace BookAggregator.Api.Models;

public sealed class BookSearchRequest : IValidatableObject
{
    [Required, StringLength(200)]
    public string Query { get; init; } = "";

    [StringLength(50, MinimumLength = 1)]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Source { get; init; }

    [StringLength(200, MinimumLength = 1)]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Author { get; init; }

    [StringLength(200, MinimumLength = 1)]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Category { get; init; }

    [Range(1, 9999)]
    public int? MinYear { get; init; }

    [Range(1, 9999)]
    public int? MaxYear { get; init; }

    [Required, RegularExpression("(?i)^(relevance|title|publishedYear|source)$")]
    public string SortBy { get; init; } = "relevance";

    [Required, RegularExpression("(?i)^(asc|desc)$")]
    public string SortOrder { get; init; } = "asc";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinYear > MaxYear)
            yield return new ValidationResult("minYear must not exceed maxYear.", [nameof(MinYear), nameof(MaxYear)]);

        if (Source is not null && string.IsNullOrWhiteSpace(Source))
            yield return new ValidationResult("source must not be blank.", [nameof(Source)]);
        if (Author is not null && string.IsNullOrWhiteSpace(Author))
            yield return new ValidationResult("author must not be blank.", [nameof(Author)]);
        if (Category is not null && string.IsNullOrWhiteSpace(Category))
            yield return new ValidationResult("category must not be blank.", [nameof(Category)]);
    }
}
