using System.Text.Json;
using System.Text.Json.Serialization;

namespace StealthEye.Runtime;

public static class EyeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static readonly JsonSerializerOptions Compact = new(Options)
    {
        WriteIndented = false,
    };
}
