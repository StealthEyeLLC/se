using System.Text.Json;

namespace StealthEye.Runtime;

public sealed class ArgReader(IReadOnlyDictionary<string, JsonElement>? args)
{
    private readonly IReadOnlyDictionary<string, JsonElement> _args = args ?? new Dictionary<string, JsonElement>();

    public bool Has(string name) => _args.ContainsKey(name);

    public string? String(string name, string? defaultValue = null)
    {
        if (!_args.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return defaultValue;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public string RequireString(string name)
    {
        var value = String(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required string argument '{name}'.");
        }
        return value;
    }

    public int Int32(string name, int defaultValue = 0)
    {
        if (!_args.TryGetValue(name, out var value)) return defaultValue;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : int.TryParse(value.ToString(), out result) ? result : defaultValue;
    }

    public long Int64(string name, long defaultValue = 0)
    {
        if (!_args.TryGetValue(name, out var value)) return defaultValue;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : long.TryParse(value.ToString(), out result) ? result : defaultValue;
    }

    public bool Boolean(string name, bool defaultValue = false)
    {
        if (!_args.TryGetValue(name, out var value)) return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    public IReadOnlyList<string> Strings(string name)
    {
        if (!_args.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString()).ToArray();
    }

    public IReadOnlyDictionary<string, string> StringMap(string name)
    {
        if (!_args.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }
        return value.EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? string.Empty : p.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
    }
}
