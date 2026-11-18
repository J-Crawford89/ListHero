using System.Globalization;

namespace ListHero.UI.Components;

internal static class PageQuery
{
    public static bool TryParse(string[]? values, int maximum, out int page)
    {
        page = 1;
        if (values is null || values.Length == 0) return true;
        if (values.Length != 1 || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1 || parsed > maximum) return false;
        page = parsed;
        return true;
    }
}
