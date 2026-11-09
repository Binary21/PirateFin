using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Emby.Server.Implementations.PirateFin.Providers;

/// <summary>
/// Null-safe accessors for provider JSON.
/// </summary>
internal static class JsonElementExtensions
{
    public static string GetString(this JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return string.Empty;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
    }

    public static int? GetInt(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
                ? result
                : null;

    public static long? GetLong(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var result)
                ? result
                : null;

    public static double GetDouble(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var result)
                ? result
                : 0;

    public static string[] GetStringArray(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)
                    .ToArray()
                : [];

    // Reads an ISO 8601 timestamp as UTC.
    public static DateTime? GetUtcDateTime(this JsonElement element, string name)
    {
        var text = element.GetString(name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    // Reads unix seconds as UTC.
    public static DateTime? GetUnixUtcDateTime(this JsonElement element, string name)
        => element.GetLong(name) is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    public static bool TryGetArray(this JsonElement element, string name, out JsonElement array)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out array)
            && array.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        array = default;
        return false;
    }
}
