using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.LibraryImporter.Engine;

/// <summary>
/// Safe <see cref="JsonElement"/> accessors.
///
/// System.Text.Json's TryGetInt32/TryGetDouble do NOT return false on a type mismatch —
/// they only return false for numeric overflow. If the element's <see cref="JsonValueKind"/>
/// is String or Null they THROW InvalidOperationException ("requires an element of type
/// 'Number', but the target element has type 'String'/'Null'"). TVDB and TMDB routinely
/// return numeric-looking fields (year, runtime, vote_average, ids) as strings or null, so a
/// raw TryGetInt32 there crashes the whole lookup. These helpers check ValueKind first and
/// fall back to invariant string parsing, and never throw on type mismatch.
/// </summary>
internal static class JsonSafe
{
    /// <summary>Returns the string value of <paramref name="prop"/>, or null if absent/not a string.</summary>
    public static string? GetStringOrNull(this JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(prop, out var p)
            && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

    /// <summary>Reads <paramref name="prop"/> as an int, accepting JSON numbers or numeric strings.</summary>
    public static bool TryGetInt(this JsonElement el, string prop, out int value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p)) return false;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    /// <summary>Reads <paramref name="prop"/> as a double, accepting JSON numbers or numeric strings.</summary>
    public static bool TryGetDoubleSafe(this JsonElement el, string prop, out double value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p)) return false;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    /// <summary>
    /// Enumerates the array at <paramref name="prop"/>, or yields nothing if it is absent or
    /// not an array. JsonElement.EnumerateArray() throws InvalidOperationException when the
    /// element is an Object/null (e.g. TVDB's "companies" is an object, not an array).
    /// </summary>
    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement el, string prop)
    {
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(prop, out var p)
            && p.ValueKind == JsonValueKind.Array)
        {
            return p.EnumerateArray();
        }

        return Array.Empty<JsonElement>();
    }

    /// <summary>Enumerates <paramref name="el"/> itself when it is an array, otherwise yields nothing.</summary>
    public static IEnumerable<JsonElement> AsArrayOrEmpty(this JsonElement el) =>
        el.ValueKind == JsonValueKind.Array ? el.EnumerateArray() : Array.Empty<JsonElement>();

    /// <summary>Reads an id field that may arrive as a JSON number or string, returned as a string.</summary>
    public static string? GetIdString(this JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(prop, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : p.GetRawText(),
            _ => null,
        };
    }
}
