using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ListHero.Contracts.Products;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ListHero.Infrastructure.Products;

public sealed class ProductHtmlParser
{
    public ProductLinkResult Parse(string html, Uri url, string transport)
    {
        var document = new HtmlParser().ParseDocument(html);
        var title = Clean(document.Title, 200);
        var blocked = title is not null && Regex.IsMatch(title, @"access denied|captcha|robot check|just a moment|verify.{0,15}human|unusual traffic", RegexOptions.IgnoreCase);
        if (blocked) return new("unavailable");
        string? Meta(string key) => document.QuerySelectorAll("meta").FirstOrDefault(e =>
            string.Equals(e.GetAttribute("property") ?? e.GetAttribute("name"), key, StringComparison.OrdinalIgnoreCase))?.GetAttribute("content");
        var amazon = url.IdnHost == "amazon.com" || url.IdnHost.EndsWith(".amazon.com", StringComparison.OrdinalIgnoreCase);
        string? name = amazon ? document.QuerySelector("#productTitle")?.TextContent : null;
        string? image = amazon ? document.QuerySelector("#landingImage")?.GetAttribute("data-old-hires") ?? document.QuerySelector("#landingImage")?.GetAttribute("src") : null;
        string? description = null; double? price = null;
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']").Take(12))
        {
            if (script.TextContent.Length > 256 * 1024) continue;
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, new() { MaxDepth = 32 });
                foreach (var product in Products(json.RootElement))
                {
                    name ??= Text(product, "name"); description ??= Text(product, "description");
                    if (product.TryGetProperty("image", out var value)) image ??= Image(value);
                    if (product.TryGetProperty("offers", out var offers))
                    {
                        if (offers.ValueKind == JsonValueKind.Array) offers = offers.EnumerateArray().FirstOrDefault();
                        if (Text(offers, "priceCurrency") == "USD" && double.TryParse(Text(offers, "price"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) && double.IsFinite(amount) && amount >= 0)
                            price ??= amount;
                    }
                }
            }
            catch (JsonException) { }
        }
        name = Clean(name ?? Meta("og:title") ?? Meta("twitter:title") ?? title, 200);
        // A generic Amazon response and a guessed URL slug are not product metadata.
        if (amazon && (name is null || !Regex.IsMatch(name, @"\s") || Regex.IsMatch(name, @"^Amazon\.com(?:\s*:.*)?$", RegexOptions.IgnoreCase))) return new("unavailable");
        description = Clean(description ?? Meta("og:description") ?? Meta("description"), 4000);
        image = new[] { image, Meta("og:image:secure_url"), Meta("og:image"), Meta("twitter:image") }
            .Select(value => ResolveImage(value, url)).FirstOrDefault(value => value is not null);
        if (name is not null && Regex.IsMatch(name, @"^(home|welcome|access denied|just a moment)[.! ]*$", RegexOptions.IgnoreCase)) return new("unavailable");
        var result = new ProductLinkResult("found", name, description, image, price, transport);
        return result.HasDetails && (name is not null || image is not null) ? result : new("unavailable");
    }
    private static IEnumerable<JsonElement> Products(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array) { foreach (var child in element.EnumerateArray()) foreach (var product in Products(child)) yield return product; }
        if (element.ValueKind != JsonValueKind.Object) yield break;
        if (element.TryGetProperty("@type", out var type) && (type.ValueKind == JsonValueKind.String && type.GetString() == "Product" || type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "Product"))) yield return element;
        if (element.TryGetProperty("@graph", out var graph)) foreach (var product in Products(graph)) yield return product;
    }
    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : null;
    private static string? Image(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.Object => Text(value, "url") ?? Text(value, "contentUrl"),
        JsonValueKind.Array => value.EnumerateArray().Select(Image).FirstOrDefault(v => v is not null), _ => null
    };
    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Imported markup is reduced to text, never rendered as HTML by the client.
        var text = new HtmlParser().ParseDocument(value).Body?.TextContent ?? "";
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text[..Math.Min(text.Length, max)];
    }
    private static string? ResolveImage(string? value, Uri page)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(page, value, out var uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns ||
            Regex.IsMatch(uri.AbsolutePath, @"logo|captcha|akamai|favicon|placeholder|sprite", RegexOptions.IgnoreCase)) return null;
        try { return new PublicProductNetwork().Validate(uri.AbsoluteUri).AbsoluteUri; }
        catch (ArgumentException) { return null; }
    }
}
