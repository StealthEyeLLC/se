using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StealthEye.Configuration;
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
        string? transientImagePath = null;
        var effectiveArgs = args;
        if (op.Equals("browser.screenshot", StringComparison.Ordinal) && !HasPath(args))
        {
            var runRoot = Path.GetDirectoryName(EyeConfig.Load().UserProcessOutputDirectory)
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var imageRoot = Path.Combine(runRoot, "mcp-images");
            Directory.CreateDirectory(imageRoot);
            transientImagePath = Path.Combine(imageRoot, Guid.NewGuid().ToString("N") + ".img");
            effectiveArgs = args is null
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(args, StringComparer.OrdinalIgnoreCase);
            effectiveArgs["path"] = JsonSerializer.SerializeToElement(transientImagePath, EyeJson.Options);
        }

        try
        {
            var envelope = await dispatcher.DispatchAsync(op, effectiveArgs, cancellationToken);
            var content = new List<ContentBlock>
            {
                new TextContentBlock { Text = JsonSerializer.Serialize(envelope, EyeJson.Compact) },
            };

            if (envelope.Ok)
            {
                if (envelope.Result is ScreenCaptureResult capture)
                {
                    content.Add(ImageContentBlock.FromBytes(capture.ImageBytes, capture.MimeType));
                }
                else if (IsImageOperation(op) && TryReadRoutedImage(envelope.Result, out var imageBytes, out var mimeType))
                {
                    content.Add(ImageContentBlock.FromBytes(imageBytes, mimeType));
                }
            }

            return new CallToolResult
            {
                Content = content,
                StructuredContent = JsonSerializer.SerializeToElement(envelope, EyeJson.Options),
                IsError = !envelope.Ok,
            };
        }
        finally
        {
            if (transientImagePath is not null)
            {
                try { File.Delete(transientImagePath); } catch { }
            }
        }
    }

    private static bool IsImageOperation(string op) =>
        op.Equals("screen.capture", StringComparison.Ordinal)
        || op.Equals("browser.screenshot", StringComparison.Ordinal);

    private static bool HasPath(IReadOnlyDictionary<string, JsonElement>? args) =>
        args is not null
        && args.TryGetValue("path", out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool TryReadRoutedImage(object? result, out byte[] bytes, out string mimeType)
    {
        bytes = [];
        mimeType = "image/png";
        if (result is null) return false;

        JsonElement json;
        try
        {
            json = result is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(result, EyeJson.Options);
        }
        catch
        {
            return false;
        }

        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("saved_path", out var pathValue)
            || pathValue.ValueKind != JsonValueKind.String)
            return false;

        var path = pathValue.GetString();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (json.TryGetProperty("mime_type", out var mimeValue) && mimeValue.ValueKind == JsonValueKind.String)
            mimeType = mimeValue.GetString() ?? mimeType;
        bytes = File.ReadAllBytes(path);
        return bytes.Length > 0;
    }
}
