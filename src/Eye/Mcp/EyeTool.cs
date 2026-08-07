using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StealthEye.Operations;
using StealthEye.Runtime;
using StealthEye.Windows;

namespace StealthEye.Mcp;

[McpServerToolType]
public sealed class EyeTool(OperationDispatcher dispatcher)
{
    [McpServerTool(Name = "eye", UseStructuredContent = true)]
    [Description("Operate the owner's StealthEye Windows workstation through one stable native tool. Select an operation with op and pass operation-specific fields in args.")]
    public async Task<CallToolResult> CallAsync(
        [Description("StealthEye operation name, such as capabilities, system.info, run, screen.capture, ui.find, or file.read.")] string op,
        [Description("Operation-specific arguments. Omit when the selected operation takes no arguments.")] Dictionary<string, JsonElement>? args = null,
        CancellationToken cancellationToken = default)
    {
        var envelope = await dispatcher.DispatchAsync(op, args, cancellationToken);
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = JsonSerializer.Serialize(envelope, EyeJson.Compact) },
        };
        if (envelope.Ok && envelope.Result is ScreenCaptureResult capture)
            content.Add(ImageContentBlock.FromBytes(capture.ImageBytes, capture.MimeType));

        return new CallToolResult
        {
            Content = content,
            StructuredContent = JsonSerializer.SerializeToElement(envelope, EyeJson.Options),
            IsError = !envelope.Ok,
        };
    }
}
