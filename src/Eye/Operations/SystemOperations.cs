using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using StealthEye.Configuration;
using StealthEye.Runtime;

namespace StealthEye.Operations;

public sealed class SystemOperations(EyeConfig config, EyeRuntimeContext runtime)
{
    private static readonly string[] OperationNames =
    [
        "capabilities", "system.info", "system.status", "system.doctor",
        "run", "wsl.run",
        "process.start", "process.read", "process.write", "process.stat", "process.list", "process.stop", "process.remove",
        "file.read", "file.write", "file.list", "file.stat", "file.mkdir", "file.copy", "file.move", "file.remove", "file.hash",
        "session.info"
    ];

    public object Capabilities() => new
    {
        product = "StealthEye",
        executable = "eye",
        version = VersionInfo.Version,
        tool = "eye",
        schema = new { op = "string", args = new { } },
        operations = OperationNames,
        transport = new { type = "streamable_http", endpoint = $"http://{config.ListenAddress}:{config.Port}{config.McpPath}", stateless = true },
        runtime = new
        {
            mode = runtime.Mode.ToString().ToLowerInvariant(),
            service_mode = runtime.IsService,
            session_mode = runtime.IsSession,
            session_pipe = config.PipeName,
            process_handles = true,
            concurrent_requests = true,
            desktop = "planned",
            browser = "planned",
            unity = "planned",
            unreal = "planned",
            models = "planned",
        },
    };

    public object Info()
    {
        string identity;
        try { identity = WindowsIdentity.GetCurrent().Name; } catch { identity = Environment.UserName; }
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new
        {
            name = d.Name,
            format = d.DriveFormat,
            type = d.DriveType.ToString(),
            total_bytes = d.TotalSize,
            free_bytes = d.AvailableFreeSpace,
            label = d.VolumeLabel,
        }).ToArray();

        return new
        {
            product = "StealthEye",
            version = VersionInfo.Version,
            machine = Environment.MachineName,
            identity,
            user = Environment.UserName,
            os = RuntimeInformation.OSDescription,
            os_architecture = RuntimeInformation.OSArchitecture.ToString(),
            process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            processor_count = Environment.ProcessorCount,
            system_directory = Environment.SystemDirectory,
            current_directory = Environment.CurrentDirectory,
            is_64_bit_os = Environment.Is64BitOperatingSystem,
            is_64_bit_process = Environment.Is64BitProcess,
            interactive = Environment.UserInteractive,
            process_id = Environment.ProcessId,
            drives,
        };
    }

    public object Status() => new
    {
        version = VersionInfo.Version,
        process_id = Environment.ProcessId,
        started_at = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        config_path = EyeConfig.DefaultPath,
        endpoint = $"http://{config.ListenAddress}:{config.Port}{config.McpPath}",
        pipe_name = config.PipeName,
        mode = runtime.Mode.ToString().ToLowerInvariant(),
    };

    public object Doctor()
    {
        var checks = new List<object>
        {
            CheckPath("X drive", "X:\\"),
            CheckPath("E drive", "E:\\"),
            CheckCommand("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"]),
            CheckCommand("git.exe", ["--version"]),
            CheckCommand("wsl.exe", ["--status"]),
            CheckCommand("docker.exe", ["version", "--format", "{{.Client.Version}}"]),
            CheckCommand("nvidia-smi.exe", ["--query-gpu=name,driver_version,memory.total", "--format=csv,noheader"]),
        };
        return new { checks };
    }

    private static object CheckPath(string name, string path) => new { name, ok = Directory.Exists(path), detail = path };

    private static object CheckCommand(string command, IReadOnlyList<string> args)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return new { name = command, ok = false, detail = "timeout" };
            }
            var output = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
            return new { name = command, ok = process.ExitCode == 0, exit_code = process.ExitCode, detail = output };
        }
        catch (Exception ex)
        {
            return new { name = command, ok = false, detail = ex.Message };
        }
    }
}

public static class VersionInfo
{
    public const string Version = "0.1.0";
}
