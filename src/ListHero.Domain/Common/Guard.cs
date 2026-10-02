namespace ListHero.Domain.Common;

internal static class Guard
{
    public static string Required(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        value = value.Trim();
        if (value.Length > maxLength)
            throw new ArgumentOutOfRangeException(parameterName, $"Must be at most {maxLength} characters.");
        return value;
    }

    public static Guid Id(Guid value, string parameterName) => value != Guid.Empty
        ? value : throw new ArgumentException("An identifier is required.", parameterName);

    public static string? WebUrl(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Must be an absolute HTTP or HTTPS URL of at most 2048 characters.", parameterName);
        return value;
    }

    public static string Description(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 4000) throw new ArgumentOutOfRangeException(nameof(value), "Must be at most 4000 characters.");
        return value.Trim();
    }
}
