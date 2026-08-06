using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StealthEye.Configuration;
using StealthEye.Operations;
using StealthEye.Runtime;
using StealthEye.Windows;
using Xunit;

namespace StealthEye.Tests;

public sealed class OperationDispatcherTests
{
    [Fact]
    public async Task CapabilitiesExposeSingleToolFoundation()
    {
        await using var services = BuildServices();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync("capabilities", null, CancellationToken.None);
        Assert.True(response.Ok);
        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains("\"tool\":\"eye\"", json);
        Assert.Contains("\"run\"", json);
    }

    [Fact]
    public async Task FileRoundTripWorks()
    {
        await using var services = BuildServices();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var root = Path.Combine(Path.GetTempPath(), "StealthEye.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "test.txt");
        try
        {
            var writeResponse = await dispatcher.DispatchAsync("file.write", Args(new { path, content = "eye" }), CancellationToken.None);
            var readResponse = await dispatcher.DispatchAsync("file.read", Args(new { path }), CancellationToken.None);
            Assert.True(writeResponse.Ok);
            Assert.True(readResponse.Ok);
            Assert.Contains("eye", JsonSerializer.Serialize(readResponse.Result));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RawPowerShellExecutionReturnsNativeOutput()
    {
        await using var services = BuildServices();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync(
            "run",
            Args(new { shell = "powershell", command = "Write-Output EYE_RUN_OK; Start-Sleep -Milliseconds 150" }),
            CancellationToken.None);
        Assert.True(response.Ok);
        using var result = JsonSerializer.SerializeToDocument(response.Result);
        Assert.Equal(0, result.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Contains("EYE_RUN_OK", result.RootElement.GetProperty("stdout").GetString());
        Assert.True(result.RootElement.GetProperty("job_assigned").GetBoolean());
    }

    [Fact]
    public async Task ConPtyTerminalAcceptsInputAndResize()
    {
        await using var services = BuildServices();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var started = await dispatcher.DispatchAsync(
            "terminal.open",
            Args(new { shell = "cmd", columns = 100, rows = 25 }),
            TestContext.Current.CancellationToken);
        Assert.True(started.Ok, JsonSerializer.Serialize(started.Error));
        using var startedJson = JsonSerializer.SerializeToDocument(started.Result);
        var handle = startedJson.RootElement.GetProperty("handle").GetString()!;
        Assert.True(startedJson.RootElement.GetProperty("interactive").GetBoolean());
        Assert.NotEqual("exited", startedJson.RootElement.GetProperty("state").GetString());
        Assert.True(startedJson.RootElement.GetProperty("job_assigned").GetBoolean(), JsonSerializer.Serialize(started.Result));

        var resized = await dispatcher.DispatchAsync(
            "terminal.resize",
            Args(new { handle, columns = 132, rows = 40 }),
            TestContext.Current.CancellationToken);
        Assert.True(resized.Ok, JsonSerializer.Serialize(resized.Error));

        var written = await dispatcher.DispatchAsync(
            "terminal.write",
            Args(new { handle, text = "echo CONPTY_OK\r\nexit\r\n" }),
            TestContext.Current.CancellationToken);
        Assert.True(written.Ok, JsonSerializer.Serialize(written.Error));
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        var read = await dispatcher.DispatchAsync(
            "terminal.read",
            Args(new { handle, stdout_offset = 0, stderr_offset = 0, max_bytes = 65536 }),
            TestContext.Current.CancellationToken);
        Assert.True(read.Ok, JsonSerializer.Serialize(read.Error));
        using var readJson = JsonSerializer.SerializeToDocument(read.Result);
        var terminalOutput = readJson.RootElement.GetProperty("stdout").GetString() ?? string.Empty;
        Assert.True(terminalOutput.Contains("CONPTY_OK", StringComparison.Ordinal), JsonSerializer.Serialize(read.Result));

        var removed = await dispatcher.DispatchAsync(
            "terminal.remove",
            Args(new { handle }),
            TestContext.Current.CancellationToken);
        Assert.True(removed.Ok, JsonSerializer.Serialize(removed.Error));
    }

    [Fact]
    public async Task ProcessHandleKeepsCapturingAfterStarterTokenIsCancelled()
    {
        await using var services = BuildServices();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        using var starter = new CancellationTokenSource();
        var started = await dispatcher.DispatchAsync(
            "process.start",
            Args(new
            {
                shell = "powershell",
                command = "Write-Output PROCESS_BEGIN; Start-Sleep -Milliseconds 300; Write-Output PROCESS_END"
            }),
            starter.Token);
        Assert.True(started.Ok);
        using var startedJson = JsonSerializer.SerializeToDocument(started.Result);
        var handle = startedJson.RootElement.GetProperty("handle").GetString()!;
        starter.Cancel();
        await Task.Delay(800, TestContext.Current.CancellationToken);

        var read = await dispatcher.DispatchAsync(
            "process.read",
            Args(new { handle, stdout_offset = 0, stderr_offset = 0, max_bytes = 65536 }),
            CancellationToken.None);
        Assert.True(read.Ok);
        using var readJson = JsonSerializer.SerializeToDocument(read.Result);
        Assert.Contains("PROCESS_BEGIN", readJson.RootElement.GetProperty("stdout").GetString());
        Assert.Contains("PROCESS_END", readJson.RootElement.GetProperty("stdout").GetString());
        Assert.Equal("exited", readJson.RootElement.GetProperty("process").GetProperty("state").GetString());

        var removed = await dispatcher.DispatchAsync("process.remove", Args(new { handle }), CancellationToken.None);
        Assert.True(removed.Ok);
    }

    [Fact]
    public async Task ServiceRoutesUserExecutionAndHandlesThroughSessionPipe()
    {
        var pipeName = "StealthEye.Tests." + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "StealthEye.Tests", Guid.NewGuid().ToString("N"));
        var config = new EyeConfig
        {
            PipeName = pipeName,
            ProcessOutputDirectory = Path.Combine(root, "system-processes"),
            UserProcessOutputDirectory = Path.Combine(root, "user-processes")
        };
        await using var sessionServices = new ServiceCollection()
            .AddLogging()
            .AddEyeCore(config, EyeRuntimeMode.Session)
            .BuildServiceProvider();
        await using var serviceServices = new ServiceCollection()
            .AddLogging()
            .AddEyeCore(config, EyeRuntimeMode.Service)
            .BuildServiceProvider();
        var sessionDispatcher = sessionServices.GetRequiredService<OperationDispatcher>();
        var serviceDispatcher = serviceServices.GetRequiredService<OperationDispatcher>();
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var serverTask = new SessionPipeServer(pipeName, sessionDispatcher).RunAsync(serverCancellation.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        try
        {
            var run = await serviceDispatcher.DispatchAsync(
                "run",
                Args(new { context = "user", shell = "powershell", command = "Write-Output SESSION_ROUTE_OK" }),
                TestContext.Current.CancellationToken);
            Assert.True(run.Ok, JsonSerializer.Serialize(run.Error));
            using var runJson = JsonSerializer.SerializeToDocument(run.Result);
            Assert.Contains("SESSION_ROUTE_OK", runJson.RootElement.GetProperty("stdout").GetString());

            var started = await serviceDispatcher.DispatchAsync(
                "process.start",
                Args(new
                {
                    context = "interactive",
                    shell = "powershell",
                    command = "Write-Output USER_PROCESS_BEGIN; Start-Sleep -Milliseconds 250; Write-Output USER_PROCESS_END"
                }),
                TestContext.Current.CancellationToken);
            Assert.True(started.Ok);
            using var startedJson = JsonSerializer.SerializeToDocument(started.Result);
            var handle = startedJson.RootElement.GetProperty("handle").GetString()!;
            Assert.StartsWith("proc_u_", handle);
            await Task.Delay(700, TestContext.Current.CancellationToken);

            var read = await serviceDispatcher.DispatchAsync(
                "process.read",
                Args(new { handle, stdout_offset = 0, stderr_offset = 0, max_bytes = 65536 }),
                TestContext.Current.CancellationToken);
            Assert.True(read.Ok);
            using var readJson = JsonSerializer.SerializeToDocument(read.Result);
            Assert.Contains("USER_PROCESS_END", readJson.RootElement.GetProperty("stdout").GetString());

            var removed = await serviceDispatcher.DispatchAsync(
                "process.remove",
                Args(new { handle }),
                TestContext.Current.CancellationToken);
            Assert.True(removed.Ok);
        }
        finally
        {
            serverCancellation.Cancel();
            await serverTask;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static ServiceProvider BuildServices()
    {
        var root = Path.Combine(Path.GetTempPath(), "StealthEye.Tests", Guid.NewGuid().ToString("N"));
        var config = new EyeConfig
        {
            ProcessOutputDirectory = Path.Combine(root, "system-processes"),
            UserProcessOutputDirectory = Path.Combine(root, "user-processes")
        };
        return new ServiceCollection().AddLogging().AddEyeCore(config).BuildServiceProvider();
    }
}
