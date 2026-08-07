using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using StealthEye.Configuration;
using StealthEye.Mcp;
using StealthEye.Operations;
using StealthEye.Runtime;
using Xunit;

namespace StealthEye.Tests;

public sealed class VisionUiContractTests
{
    [Fact]
    public async Task CapabilitiesExposeCaptureAndSemanticUiWithoutNewTools()
    {
        await using var services = BuildServices();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync("capabilities", null, TestContext.Current.CancellationToken);
        Assert.True(response.Ok);
        var json = JsonSerializer.Serialize(response.Result);
        Assert.Contains("screen.capture", json);
        Assert.Contains("ui.find", json);
        Assert.Contains("ui.invoke", json);
        Assert.Contains("0.4.0", json);
    }

    [Fact]
    public async Task InvalidCaptureTargetFailsBeforeReadingPixels()
    {
        await using var services = BuildServices();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync(
            "screen.capture",
            Args(new { target = "not-a-real-target" }),
            TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal("invalid_arguments", response.Error?.Code);
    }

    [Fact]
    public async Task InvalidUiActionFailsWithoutPersistingElementState()
    {
        await using var services = BuildServices();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var response = await dispatcher.DispatchAsync("ui.focus", null, TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal("invalid_arguments", response.Error?.Code);
    }

    [Fact]
    public async Task McpToolKeepsStructuredEnvelopeAndTextContent()
    {
        await using var services = BuildServices();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        var tool = new EyeTool(dispatcher);
        var result = await tool.CallAsync("capabilities", null, TestContext.Current.CancellationToken);
        Assert.False(result.IsError ?? false);
        Assert.NotNull(result.StructuredContent);
        Assert.Contains(result.Content, item => item is TextContentBlock text && text.Text.Contains("\"ok\":true", StringComparison.Ordinal));
        Assert.True(result.StructuredContent!.Value.GetProperty("ok").GetBoolean());
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    private static ServiceProvider BuildServices()
    {
        var root = Path.Combine(Path.GetTempPath(), "StealthEye.Tests", Guid.NewGuid().ToString("N"));
        return new ServiceCollection()
            .AddLogging()
            .AddEyeCore(new EyeConfig
            {
                ProcessOutputDirectory = Path.Combine(root, "system-processes"),
                UserProcessOutputDirectory = Path.Combine(root, "user-processes")
            })
            .BuildServiceProvider();
    }
}
