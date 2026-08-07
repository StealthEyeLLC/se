using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting.WindowsServices;
using ModelContextProtocol.Server;
using StealthEye.Configuration;
using StealthEye.Mcp;
using StealthEye.Operations;
using StealthEye.Runtime;
using StealthEye.Windows;

return await EyeProgram.RunAsync(args);

internal static class EyeProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        var command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        return command switch
        {
            "--version" or "-v" or "version" => PrintVersion(),
            "serve" or "service" => await RunServerAsync(args.Skip(1).ToArray()),
            "session" => await RunSessionAsync(),
            "call" => await RunCallAsync(args.Skip(1).ToArray()),
            "status" => await RunCallAsync(["system.status"]),
            "doctor" => await RunCallAsync(["system.doctor"]),
            "help" or "--help" or "-h" => PrintHelp(),
            _ => PrintUnknown(command),
        };
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"StealthEye eye {VersionInfo.Version}");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
StealthEye eye

Usage:
  eye --version
  eye serve
  eye session
  eye call <op> [json-args]
  eye call <op> --args-file <path>
  eye call <op> -    # read JSON args from stdin
  eye status
  eye doctor

Examples:
  eye call capabilities
  eye call system.info
  eye call run '{"shell":"powershell","command":"Get-Process | Select-Object -First 5"}'
""");
        return 0;
    }

    private static int PrintUnknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run 'eye help'.");
        return 2;
    }

    private static async Task<int> RunServerAsync(string[] args)
    {
        var config = EyeConfig.Load();
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(options => options.ServiceName = "StealthEye");
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Parse(config.ListenAddress), config.Port));
        builder.Services.AddEyeCore(config, EyeRuntimeMode.Service);
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<EyeTool>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!string.IsNullOrWhiteSpace(config.LocalToken) && context.Request.Path.StartsWithSegments(config.McpPath))
            {
                var supplied = context.Request.Headers.Authorization.ToString();
                if (!string.Equals(supplied, "Bearer " + config.LocalToken, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                    return;
                }
            }
            await next();
        });
        app.MapGet("/healthz", () => Results.Json(new { ok = true, product = "StealthEye", version = VersionInfo.Version }));
        app.MapGet("/readyz", () => Results.Json(new { ok = true, mcp = config.McpPath }));
        app.MapMcp(config.McpPath);
        await app.RunAsync();
        return 0;
    }

    private static async Task<int> RunSessionAsync()
    {
        HideSessionConsoleWindow();
        var config = EyeConfig.Load();
        Console.WriteLine($"StealthEye session helper listening on pipe '{config.PipeName}'.");
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddEyeCore(config, EyeRuntimeMode.Session)
            .BuildServiceProvider();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        await new SessionPipeServer(config.PipeName, dispatcher).RunAsync(shutdown.Token);
        return 0;
    }

    private static async Task<int> RunCallAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: eye call <op> [json-args]");
            return 2;
        }

        Dictionary<string, JsonElement>? operationArgs = null;
        string? rawArgs;
        try
        {
            rawArgs = await ReadOperationArgsAsync(args);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Unable to read operation arguments: {ex.Message}");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(rawArgs))
        {
            try
            {
                operationArgs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(rawArgs, EyeJson.Options);
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Invalid JSON arguments: {ex.Message}");
                return 2;
            }
        }

        var config = EyeConfig.Load();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddEyeCore(config, EyeRuntimeMode.Cli)
            .BuildServiceProvider();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync(args[0], operationArgs, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(response, EyeJson.Options));
        return response.Ok ? 0 : 1;
    }

    private static async Task<string?> ReadOperationArgsAsync(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1])) return null;
        if (args[1] == "-") return await Console.In.ReadToEndAsync();
        if (args[1].Equals("--args-file", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2]))
                throw new ArgumentException("--args-file requires a path.");
            return await File.ReadAllTextAsync(Path.GetFullPath(args[2]));
        }
        if (args[1].StartsWith('@') && args[1].Length > 1)
            return await File.ReadAllTextAsync(Path.GetFullPath(args[1][1..]));
        return args[1];
    }

    private static void HideSessionConsoleWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var window = GetConsoleWindow();
        if (window != 0) _ = ShowWindow(window, 0);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

}
