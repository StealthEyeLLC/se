using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StealthEye.Configuration;
using StealthEye.Operations;
using StealthEye.Runtime;
using StealthEye.Windows;
using Xunit;

namespace StealthEye.Tests;

public sealed class DesktopOperationsTests
{
    [Fact]
    public async Task CapabilitiesExposeDesktopVocabulary()
    {
        await using var services = BuildServices(new EyeConfig(), EyeRuntimeMode.Cli);
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync("capabilities", null, TestContext.Current.CancellationToken);
        Assert.True(response.Ok);
        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains("\"display.list\"", json);
        Assert.Contains("\"window.activate\"", json);
        Assert.Contains("\"pointer.click\"", json);
        Assert.Contains("\"keyboard.type\"", json);
        Assert.Contains("\"clipboard.write\"", json);
    }

    [Fact]
    public async Task ReadOnlyDesktopQueriesReturnStructuredResults()
    {
        await using var services = BuildServices(new EyeConfig(), EyeRuntimeMode.Cli);
        var dispatcher = services.GetRequiredService<OperationDispatcher>();

        var info = await dispatcher.DispatchAsync("desktop.info", null, TestContext.Current.CancellationToken);
        var displays = await dispatcher.DispatchAsync("display.list", null, TestContext.Current.CancellationToken);
        var windows = await dispatcher.DispatchAsync(
            "window.list",
            Args(new { visible_only = false, include_untitled = true, max_entries = 10 }),
            TestContext.Current.CancellationToken);

        Assert.True(info.Ok, JsonSerializer.Serialize(info.Error));
        Assert.True(displays.Ok, JsonSerializer.Serialize(displays.Error));
        Assert.True(windows.Ok, JsonSerializer.Serialize(windows.Error));
        Assert.Contains("monitor_count", JsonSerializer.Serialize(info.Result));
        Assert.Contains("monitors", JsonSerializer.Serialize(displays.Result));
        Assert.Contains("windows", JsonSerializer.Serialize(windows.Result));
    }

    [Fact]
    public async Task InvalidKeyboardKeyFailsBeforeSendingInput()
    {
        await using var services = BuildServices(new EyeConfig(), EyeRuntimeMode.Cli);
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync(
            "keyboard.key",
            Args(new { keys = new[] { "NOT_A_REAL_KEY" } }),
            TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal("invalid_arguments", response.Error?.Code);
    }

    [Fact]
    public async Task ServiceRoutesDesktopQueriesThroughSessionPipe()
    {
        var pipeName = "StealthEye.Desktop.Tests." + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "StealthEye.Tests", Guid.NewGuid().ToString("N"));
        var config = new EyeConfig
        {
            PipeName = pipeName,
            ProcessOutputDirectory = Path.Combine(root, "system-processes"),
            UserProcessOutputDirectory = Path.Combine(root, "user-processes")
        };
        await using var sessionServices = BuildServices(config, EyeRuntimeMode.Session);
        await using var serviceServices = BuildServices(config, EyeRuntimeMode.Service);
        var sessionDispatcher = sessionServices.GetRequiredService<OperationDispatcher>();
        var serviceDispatcher = serviceServices.GetRequiredService<OperationDispatcher>();
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var serverTask = new SessionPipeServer(pipeName, sessionDispatcher).RunAsync(serverCancellation.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        try
        {
            var response = await serviceDispatcher.DispatchAsync(
                "display.list",
                null,
                TestContext.Current.CancellationToken);
            Assert.True(response.Ok, JsonSerializer.Serialize(response.Error));
            Assert.Contains("monitors", JsonSerializer.Serialize(response.Result));
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

    private static ServiceProvider BuildServices(EyeConfig config, EyeRuntimeMode mode) =>
        new ServiceCollection().AddLogging().AddEyeCore(config, mode).BuildServiceProvider();
}
