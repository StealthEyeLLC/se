using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StealthEye.Configuration;
using StealthEye.Operations;
using StealthEye.Runtime;
using StealthEye.Windows;
using Xunit;

namespace StealthEye.Tests;

public sealed class BrowserContractTests
{
    [Fact]
    public async Task CapabilitiesExposeReconnectableBrowserVocabulary()
    {
        await using var services = BuildServices(new EyeConfig(), EyeRuntimeMode.Cli);
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync("capabilities", null, TestContext.Current.CancellationToken);
        Assert.True(response.Ok);
        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains("browser.start", json);
        Assert.Contains("browser.evaluate", json);
        Assert.Contains("browser.cdp", json);
        Assert.Contains("browser.screenshot", json);
        Assert.Contains("0.5.0", json);
    }

    [Fact]
    public async Task InvalidBrowserProfileFailsBeforeLaunchingAnything()
    {
        await using var services = BuildServices(new EyeConfig(), EyeRuntimeMode.Cli);
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync(
            "browser.start",
            Args(new { profile = "../not-valid" }),
            TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal("invalid_arguments", response.Error?.Code);
    }

    [Fact]
    public async Task InvalidBrowserEngineFailsBeforeLaunchingAnything()
    {
        await using var services = BuildServices(new EyeConfig(), EyeRuntimeMode.Cli);
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync(
            "browser.start",
            Args(new { profile = "contract", engine = "not-a-browser" }),
            TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal("invalid_arguments", response.Error?.Code);
    }

    [Fact]
    public async Task ServiceRoutesBrowserOperationsThroughOwnerSessionPipe()
    {
        var pipeName = "StealthEye.Browser.Tests." + Guid.NewGuid().ToString("N");
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
            var response = await serviceDispatcher.DispatchAsync("browser.list", null, TestContext.Current.CancellationToken);
            Assert.True(response.Ok, JsonSerializer.Serialize(response.Error));
            Assert.Contains("profiles", JsonSerializer.Serialize(response.Result));
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
