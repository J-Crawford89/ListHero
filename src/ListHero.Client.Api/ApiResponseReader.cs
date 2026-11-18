using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ListHero.Client.Abstractions.Api;

namespace ListHero.Client.Api;

internal static class ApiResponseReader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    public static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken ct, bool writeMayHaveSucceeded = false)
    {
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            throw InvalidResponse(writeMayHaveSucceeded, empty: true);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Options, ct)
                ?? throw InvalidResponse(writeMayHaveSucceeded, empty: true);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw InvalidResponse(writeMayHaveSucceeded, empty: false);
        }
    }

    private static ApiRequestException InvalidResponse(bool writeMayHaveSucceeded, bool empty)
        => new(writeMayHaveSucceeded
            ? "The request may have succeeded, but we couldn't read the response. Check the result before trying again."
            : empty ? "The API returned an empty response. Please try again."
                : "We couldn't read the response from List Hero. Please try again.");

    public static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            // Optional error details must never hide the HTTP status's recovery behavior.
            return null;
        }
    }
}
