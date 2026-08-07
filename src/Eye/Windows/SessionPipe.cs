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
            var pipeResponse = SessionPipeResponse.Create(response);
            await writer.WriteLineAsync(JsonSerializer.Serialize(pipeResponse, EyeJson.Compact));
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
        var response = JsonSerializer.Deserialize<SessionPipeResponse>(line, EyeJson.Compact)
            ?? throw new IOException("Invalid session response.");
        return response.Rehydrate();
    }
}

public sealed record SessionRequest(
    string Op,
    IReadOnlyDictionary<string, JsonElement>? Args);

internal sealed record SessionPipeResponse(EyeEnvelope Envelope, string? ImageBase64 = null)
{
    public static SessionPipeResponse Create(EyeEnvelope envelope) =>
        envelope.Ok && envelope.Result is ScreenCaptureResult capture
            ? new SessionPipeResponse(envelope, Convert.ToBase64String(capture.ImageBytes))
            : new SessionPipeResponse(envelope);

    public EyeEnvelope Rehydrate()
    {
        if (string.IsNullOrWhiteSpace(ImageBase64)) return Envelope;
        if (!Envelope.Ok || Envelope.Result is not JsonElement element)
            throw new IOException("Session image response is missing capture metadata.");

        var metadata = element.Deserialize<SessionCaptureMetadata>(EyeJson.Compact)
            ?? throw new IOException("Session image response metadata is invalid.");
        var bytes = Convert.FromBase64String(ImageBase64);
        var capture = new ScreenCaptureResult(
            bytes,
            metadata.MimeType,
            metadata.Width,
            metadata.Height,
            metadata.Target,
            metadata.Backend,
            metadata.SourceX,
            metadata.SourceY,
            metadata.SourceWidth,
            metadata.SourceHeight,
            metadata.SavedPath,
            metadata.Sha256);
        return Envelope with { Result = capture };
    }
}

internal sealed record SessionCaptureMetadata(
    string MimeType,
    int Width,
    int Height,
    string Target,
    string Backend,
    int SourceX,
    int SourceY,
    int SourceWidth,
    int SourceHeight,
    string? SavedPath,
    string Sha256);

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
