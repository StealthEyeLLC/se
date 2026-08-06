using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StealthEye.Runtime;

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string? StandardInput,
    int TimeoutMs,
    int MaxOutputBytes,
    string RequestedContext)
{
    public static ProcessSpec FromArgs(IReadOnlyDictionary<string, JsonElement>? args, bool forceWsl = false)
    {
        var reader = new ArgReader(args);
        var shell = forceWsl ? "wsl" : (reader.String("shell", "direct") ?? "direct").ToLowerInvariant();
        var command = reader.String("command");
        var executable = reader.String("executable");
        var arguments = reader.Strings("arguments").ToList();

        string fileName;
        IReadOnlyList<string> finalArgs;
        switch (shell)
        {
            case "powershell":
            case "windows-powershell":
                if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("'command' is required for PowerShell execution.");
                fileName = "powershell.exe";
                finalArgs = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command];
                break;
            case "cmd":
                if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("'command' is required for cmd execution.");
                fileName = "cmd.exe";
                finalArgs = ["/d", "/s", "/c", command];
                break;
            case "wsl":
                if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("'command' is required for WSL execution.");
                fileName = "wsl.exe";
                var distro = reader.String("distribution");
                var wslArgs = new List<string>();
                if (!string.IsNullOrWhiteSpace(distro))
                {
                    wslArgs.AddRange(["--distribution", distro]);
                }
                wslArgs.AddRange(["--", "bash", "-lc", command]);
                finalArgs = wslArgs;
                break;
            case "direct":
                fileName = string.IsNullOrWhiteSpace(executable) ? reader.RequireString("file_name") : executable;
                finalArgs = arguments;
                break;
            default:
                throw new ArgumentException($"Unsupported shell '{shell}'. Expected direct, powershell, cmd, or wsl.");
        }

        return new ProcessSpec(
            fileName,
            finalArgs,
            reader.String("cwd"),
            reader.StringMap("env"),
            reader.String("stdin"),
            Math.Max(0, reader.Int32("timeout_ms", 0)),
            Math.Clamp(reader.Int32("max_output_bytes", 1024 * 1024), 1024, 64 * 1024 * 1024),
            reader.String("context", "current") ?? "current");
    }

    public ProcessStartInfo ToStartInfo(bool redirectInput = true)
    {
        var info = new ProcessStartInfo
        {
            FileName = FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in Arguments) info.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(WorkingDirectory)) info.WorkingDirectory = WorkingDirectory;
        foreach (var pair in Environment) info.Environment[pair.Key] = pair.Value;
        return info;
    }
}
