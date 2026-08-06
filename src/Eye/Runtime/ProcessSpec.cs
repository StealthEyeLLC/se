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
    string RequestedContext,
    bool Interactive,
    short Columns,
    short Rows)
{
    public static ProcessSpec FromArgs(
        IReadOnlyDictionary<string, JsonElement>? args,
        bool forceWsl = false,
        bool forceInteractive = false)
    {
        var reader = new ArgReader(args);
        var interactive = forceInteractive || reader.Boolean("interactive");
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
                fileName = "powershell.exe";
                var powershellArgs = new List<string> { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass" };
                if (!interactive) powershellArgs.Add("-NonInteractive");
                if (!string.IsNullOrWhiteSpace(command)) powershellArgs.AddRange(["-Command", command]);
                else if (!interactive) throw new ArgumentException("'command' is required for noninteractive PowerShell execution.");
                finalArgs = powershellArgs;
                break;
            case "pwsh":
                fileName = "pwsh.exe";
                var pwshArgs = new List<string> { "-NoLogo", "-NoProfile" };
                if (!interactive) pwshArgs.Add("-NonInteractive");
                if (!string.IsNullOrWhiteSpace(command)) pwshArgs.AddRange(["-Command", command]);
                else if (!interactive) throw new ArgumentException("'command' is required for noninteractive pwsh execution.");
                finalArgs = pwshArgs;
                break;
            case "cmd":
                fileName = "cmd.exe";
                if (!string.IsNullOrWhiteSpace(command)) finalArgs = ["/d", "/s", "/c", command];
                else if (interactive) finalArgs = ["/d", "/q"];
                else throw new ArgumentException("'command' is required for noninteractive cmd execution.");
                break;
            case "wsl":
                fileName = "wsl.exe";
                var distro = reader.String("distribution");
                var wslArgs = new List<string>();
                if (!string.IsNullOrWhiteSpace(distro)) wslArgs.AddRange(["--distribution", distro]);
                if (!string.IsNullOrWhiteSpace(command)) wslArgs.AddRange(["--", "bash", "-lc", command]);
                else if (interactive) wslArgs.AddRange(["--", "bash", "-l"]);
                else throw new ArgumentException("'command' is required for noninteractive WSL execution.");
                finalArgs = wslArgs;
                break;
            case "direct":
                fileName = string.IsNullOrWhiteSpace(executable) ? reader.RequireString("file_name") : executable;
                finalArgs = arguments;
                break;
            default:
                throw new ArgumentException($"Unsupported shell '{shell}'. Expected direct, powershell, pwsh, cmd, or wsl.");
        }

        return new ProcessSpec(
            fileName,
            finalArgs,
            reader.String("cwd"),
            reader.StringMap("env"),
            reader.String("stdin"),
            Math.Max(0, reader.Int32("timeout_ms", 0)),
            Math.Clamp(reader.Int32("max_output_bytes", 1024 * 1024), 1024, 64 * 1024 * 1024),
            reader.String("context", "current") ?? "current",
            interactive,
            (short)Math.Clamp(reader.Int32("columns", 120), 20, 500),
            (short)Math.Clamp(reader.Int32("rows", 30), 5, 200));
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
