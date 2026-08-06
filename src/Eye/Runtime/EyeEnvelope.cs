using System.Text.Json.Serialization;

namespace StealthEye.Runtime;

public sealed record EyeEnvelope
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EyeError? Error { get; init; }

    public static EyeEnvelope Success(object? result = null) => new() { Ok = true, Result = result ?? new { } };

    public static EyeEnvelope Failure(string message, string? code = null, object? details = null) =>
        new() { Ok = false, Error = new EyeError(message, code, details) };
}

public sealed record EyeError(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null,
    [property: JsonPropertyName("details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Details = null);
