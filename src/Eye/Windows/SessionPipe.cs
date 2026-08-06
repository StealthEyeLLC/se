using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using StealthEye.Operations;
using StealthEye.Runtime;

namespace StealthEye.Windows;

public sealed class SessionPipeServer(string pipeName, OperationDispatcher dispatcher)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                _ = HandleAsync(pipe, cancellationToken);
            }
            catch
            {
                pipe.Dispose();
                if (cancellationToken.IsCancellationRequested) break;
                throw;
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
        {
            EyeEnvelope response;
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) return;
                var request = JsonSerializer.Deserialize<SessionRequest>(line, EyeJson.Compact)
                    ?? throw new InvalidDataException("Invalid session request.");
                response = await dispatcher.DispatchAsync(request.Op, request.Args, cancellationToken);
            }
            catch (Exception ex)
            {
                response = EyeEnvelope.Failure(ex.Message, "session_request_failed");
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, EyeJson.Compact));
        }
    }
}

public sealed class SessionPipeClient(string pipeName)
{
    public async Task<EyeEnvelope> CallAsync(
        string op,
        IReadOnlyDictionary<string, JsonElement>? args,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(Math.Max(100, timeoutMs));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(linked.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new SessionRequest(op, args), EyeJson.Compact));
        var line = await reader.ReadLineAsync(linked.Token);
        if (string.IsNullOrWhiteSpace(line)) throw new IOException("Session process returned no response.");
        return JsonSerializer.Deserialize<EyeEnvelope>(line, EyeJson.Compact) ?? EyeEnvelope.Failure("Invalid session response.");
    }
}

public sealed record SessionRequest(
    string Op,
    IReadOnlyDictionary<string, JsonElement>? Args);

public static class SessionIdentity
{
    public static object GetInfo()
    {
        string identity;
        try { identity = WindowsIdentity.GetCurrent().Name; } catch { identity = Environment.UserName; }
        return new
        {
            identity,
            user = Environment.UserName,
            domain = Environment.UserDomainName,
            interactive = Environment.UserInteractive,
            session_id = Environment.ProcessId > 0 ? System.Diagnostics.Process.GetCurrentProcess().SessionId : -1,
            process_id = Environment.ProcessId,
            profile = Environment.GetEnvironmentVariable("USERPROFILE"),
        };
    }
}
