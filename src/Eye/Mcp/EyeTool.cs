using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StealthEye.Operations;
using StealthEye.Runtime;

namespace StealthEye.Mcp;

[McpServerToolType]
public sealed class EyeTool(OperationDispatcher dispatcher)
{
    [McpServerTool(Name = "eye", UseStructuredContent = true)]
    [Description("Operate the owner's StealthEye Windows workstation through one stable native tool. Select an operation with op and pass operation-specific fields in args.")]
    public Task<EyeEnvelope> CallAsync(
        [Description("StealthEye operation name, such as capabilities, system.info, run, process.start, or file.read.")] string op,
        [Description("Operation-specific arguments. Omit when the selected operation takes no arguments.")] Dictionary<string, JsonElement>? args = null,
        CancellationToken cancellationToken = default) =>
        dispatcher.DispatchAsync(op, args, cancellationToken);
}
