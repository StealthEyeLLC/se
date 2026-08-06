using System.Text.Json;
using StealthEye.Runtime;

namespace StealthEye.Configuration;

public sealed record EyeConfig
{
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 37921;
    public string McpPath { get; init; } = "/mcp";
    public string? LocalToken { get; init; }
    public string PipeName { get; init; } = "StealthEye.Session";
    public string? ProcessOutputDirectory { get; init; }
    public string? UserProcessOutputDirectory { get; init; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "StealthEye",
        "config.json");

    public static EyeConfig Load()
    {
        var path = Environment.GetEnvironmentVariable("EYE_CONFIG");
        if (string.IsNullOrWhiteSpace(path)) path = DefaultPath;
        if (!File.Exists(path)) return new EyeConfig();
        var config = JsonSerializer.Deserialize<EyeConfig>(File.ReadAllText(path), EyeJson.Options);
        return config ?? new EyeConfig();
    }

    public string ResolveProcessOutputDirectory(EyeRuntimeContext runtime)
    {
        string root;
        if (runtime.IsSession)
        {
            root = !string.IsNullOrWhiteSpace(UserProcessOutputDirectory)
                ? UserProcessOutputDirectory
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StealthEye",
                    "run",
                    "processes");
        }
        else if (!string.IsNullOrWhiteSpace(ProcessOutputDirectory))
        {
            root = ProcessOutputDirectory;
        }
        else if (runtime.IsService)
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "StealthEye",
                "run",
                "processes");
        }
        else
        {
            root = Path.Combine(Path.GetTempPath(), "StealthEye", "processes");
        }

        Directory.CreateDirectory(root);
        return root;
    }
}
