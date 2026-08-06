using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StealthEye.Configuration;
using StealthEye.Operations;
using StealthEye.Runtime;
using Xunit;

namespace StealthEye.Tests;

public sealed class NativeProcessTests
{
    [Fact]
    public async Task ConPtyTerminalAcceptsInputAndResize()
    {
        var root = Path.Combine(Path.GetTempPath(), "StealthEye.Tests", Guid.NewGuid().ToString("N"));
        var config = new EyeConfig
        {
            ProcessOutputDirectory = Path.Combine(root, "system-processes"),
            UserProcessOutputDirectory = Path.Combine(root, "user-processes")
        };
        await using var services = new ServiceCollection().AddLogging().AddEyeCore(config).BuildServiceProvider();
        var dispatcher = services.GetRequiredService<OperationDispatcher>();
        try
        {
            var started = await dispatcher.DispatchAsync(
                "process.start",
                Args(new { shell = "cmd", interactive = true, columns = 100, rows = 30 }),
                CancellationToken.None);
            Assert.True(started.Ok, JsonSerializer.Serialize(started.Error));
            using var startedJson = JsonSerializer.SerializeToDocument(started.Result);
            var handle = startedJson.RootElement.GetProperty("handle").GetString()!;
            try
            {
                var resized = await dispatcher.DispatchAsync(
                    "process.resize",
                    Args(new { handle, columns = 120, rows = 40 }),
                    CancellationToken.None);
                Assert.True(resized.Ok, JsonSerializer.Serialize(resized.Error));

                var wrote = await dispatcher.DispatchAsync(
                    "process.write",
                    Args(new { handle, text = "echo CONPTY_OK\r\nexit\r\n" }),
                    CancellationToken.None);
                Assert.True(wrote.Ok, JsonSerializer.Serialize(wrote.Error));
                await Task.Delay(1000, TestContext.Current.CancellationToken);

                var read = await dispatcher.DispatchAsync(
                    "process.read",
                    Args(new { handle, stdout_offset = 0, stderr_offset = 0, max_bytes = 65536 }),
                    CancellationToken.None);
                Assert.True(read.Ok, JsonSerializer.Serialize(read.Error));
                using var readJson = JsonSerializer.SerializeToDocument(read.Result);
                Assert.Contains("CONPTY_OK", readJson.RootElement.GetProperty("stdout").GetString());
            }
            finally
            {
                await dispatcher.DispatchAsync("process.remove", Args(new { handle }), CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
}
