using System.Text.Json;
using StealthEye.Configuration;
using StealthEye.Runtime;
using StealthEye.Windows;

namespace StealthEye.Operations;

public sealed class OperationDispatcher(
    SystemOperations system,
    FileOperations files,
    ProcessRunner runner,
    ProcessRegistry processes,
    DesktopOperations desktop,
    ScreenCaptureOperations screen,
    UiAutomationOperations ui,
    EyeConfig config,
    EyeRuntimeContext runtime)
{
    public async Task<EyeEnvelope> DispatchAsync(
        string op,
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(op)) return EyeEnvelope.Failure("Operation is required.", "invalid_operation");
        try
        {
            ValidateRequestedContext(op, args);
            if (ShouldRouteToSession(op, args))
                return await ForwardToSessionAsync(op, args, cancellationToken);

            object result = op switch
            {
                "capabilities" => system.Capabilities(),
                "system.info" => system.Info(),
                "system.status" => system.Status(),
                "system.doctor" => system.Doctor(),
                "run" => await runner.RunAsync(ProcessSpec.FromArgs(args), cancellationToken),
                "wsl.run" => await runner.RunAsync(ProcessSpec.FromArgs(args, forceWsl: true), cancellationToken),
                "process.start" => await processes.StartAsync(ProcessSpec.FromArgs(args), cancellationToken),
                "terminal.open" => await processes.StartAsync(ProcessSpec.FromArgs(args, forceInteractive: true), cancellationToken),
                "process.read" or "terminal.read" => await ProcessReadAsync(args, cancellationToken),
                "process.write" or "terminal.write" => await ProcessWriteAsync(args, cancellationToken),
                "process.resize" or "terminal.resize" => ProcessResize(args),
                "process.stat" or "terminal.stat" => processes.Stat(new ArgReader(args).RequireString("handle")),
                "process.list" or "terminal.list" => await ProcessListAsync(args, cancellationToken),
                "process.stop" or "terminal.stop" => await processes.StopAsync(new ArgReader(args).RequireString("handle"), cancellationToken),
                "process.remove" or "terminal.remove" => await processes.RemoveAsync(new ArgReader(args).RequireString("handle")),
                "file.read" => await files.ReadAsync(args, cancellationToken),
                "file.write" => await files.WriteAsync(args, cancellationToken),
                "file.list" => files.List(args),
                "file.stat" => files.Stat(args),
                "file.mkdir" => files.Mkdir(args),
                "file.copy" => files.Copy(args),
                "file.move" => files.Move(args),
                "file.remove" => files.Remove(args),
                "file.hash" => await files.HashAsync(args, cancellationToken),
                "desktop.info" => desktop.Info(),
                "display.list" => desktop.ListDisplays(),
                "window.list" => desktop.ListWindows(args),
                "window.foreground" => desktop.ForegroundWindow(),
                "window.activate" => desktop.ActivateWindow(args),
                "window.move" => desktop.MoveWindow(args),
                "window.show" => desktop.ShowWindow(args),
                "pointer.position" => desktop.PointerPosition(),
                "pointer.move" => await desktop.MovePointerAsync(args, cancellationToken),
                "pointer.click" => await desktop.ClickPointerAsync(args, cancellationToken),
                "pointer.scroll" => await desktop.ScrollPointerAsync(args, cancellationToken),
                "keyboard.type" => await desktop.TypeTextAsync(args, cancellationToken),
                "keyboard.key" => await desktop.SendKeysAsync(args, cancellationToken),
                "clipboard.read" => desktop.ReadClipboard(),
                "clipboard.write" => await desktop.WriteClipboardAsync(args, cancellationToken),
                "screen.capture" => screen.Capture(args),
                "ui.find" => ui.Find(args),
                "ui.focused" => ui.Focused(),
                "ui.from_point" => ui.FromPoint(args),
                "ui.focus" => ui.Focus(args),
                "ui.invoke" => ui.Invoke(args),
                "ui.value" => ui.Value(args),
                "ui.toggle" => ui.Toggle(args),
                "ui.select" => ui.Select(args),
                "ui.expand" => ui.Expand(args),
                "ui.scroll_into_view" => ui.ScrollIntoView(args),
                "session.info" => await SessionInfoAsync(args, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported operation '{op}'. Call capabilities for the current operation list."),
            };
            return EyeEnvelope.Success(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return EyeEnvelope.Failure("Operation was cancelled.", "cancelled");
        }
        catch (NotSupportedException ex)
        {
            return EyeEnvelope.Failure(ex.Message, "unsupported_operation");
        }
        catch (ArgumentException ex)
        {
            return EyeEnvelope.Failure(ex.Message, "invalid_arguments");
        }
        catch (Exception ex)
        {
            return EyeEnvelope.Failure(ex.Message, ex.GetType().Name, new { hresult = ex.HResult, inner = ex.InnerException?.Message });
        }
    }

    private void ValidateRequestedContext(string op, IReadOnlyDictionary<string, JsonElement>? args)
    {
        if (op is not ("run" or "wsl.run" or "process.start" or "terminal.open")) return;
        var context = (new ArgReader(args).String("context", "current") ?? "current").ToLowerInvariant();
        if (context is not ("current" or "user" or "interactive" or "system"))
            throw new ArgumentException("'context' must be current, user, interactive, or system.");
        if (context == "system" && !runtime.IsService)
            throw new NotSupportedException("System context is available through the installed StealthEye service.");
    }

    private bool ShouldRouteToSession(string op, IReadOnlyDictionary<string, JsonElement>? args)
    {
        if (!runtime.IsService) return false;
        var reader = new ArgReader(args);
        if (op == "session.info" || IsDesktopOperation(op)) return true;
        if (op == "wsl.run")
            return !string.Equals(reader.String("context", "user"), "system", StringComparison.OrdinalIgnoreCase);
        if (op is "run" or "process.start" or "terminal.open")
        {
            var context = reader.String("context", "current") ?? "current";
            if (context.Equals("user", StringComparison.OrdinalIgnoreCase)
                || context.Equals("interactive", StringComparison.OrdinalIgnoreCase)) return true;
            if (op == "terminal.open" && !context.Equals("system", StringComparison.OrdinalIgnoreCase)) return true;
            if (op == "process.start" && reader.Boolean("interactive")
                && context.Equals("current", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        if (op is "process.read" or "terminal.read" or "process.write" or "terminal.write"
            or "process.resize" or "terminal.resize" or "process.stat" or "terminal.stat"
            or "process.stop" or "terminal.stop" or "process.remove" or "terminal.remove")
            return (reader.String("handle") ?? string.Empty).StartsWith("proc_u_", StringComparison.Ordinal);
        return false;
    }

    private static bool IsDesktopOperation(string op) =>
        op.StartsWith("desktop.", StringComparison.Ordinal)
        || op.StartsWith("display.", StringComparison.Ordinal)
        || op.StartsWith("window.", StringComparison.Ordinal)
        || op.StartsWith("pointer.", StringComparison.Ordinal)
        || op.StartsWith("keyboard.", StringComparison.Ordinal)
        || op.StartsWith("clipboard.", StringComparison.Ordinal)
        || op.StartsWith("screen.", StringComparison.Ordinal)
        || op.StartsWith("ui.", StringComparison.Ordinal);

    private async Task<EyeEnvelope> ForwardToSessionAsync(
        string op,
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        return await new SessionPipeClient(config.PipeName)
            .CallAsync(op, args, SessionTimeout(op, args), cancellationToken);
    }

    private static int SessionTimeout(string op, IReadOnlyDictionary<string, JsonElement>? args)
    {
        var requested = Math.Max(0, new ArgReader(args).Int32("timeout_ms", 0));
        if (op == "run") return requested > 0 ? Math.Min(int.MaxValue, requested + 5000) : 90_000;
        return Math.Max(5000, requested > 0 ? Math.Min(int.MaxValue, requested + 5000) : 0);
    }

    private async Task<object> ProcessReadAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        return await processes.ReadAsync(
            reader.RequireString("handle"),
            Math.Max(0, reader.Int64("stdout_offset")),
            Math.Max(0, reader.Int64("stderr_offset")),
            reader.Int32("max_bytes", 1024 * 1024),
            cancellationToken);
    }

    private async Task<object> ProcessWriteAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        return await processes.WriteAsync(
            reader.RequireString("handle"),
            reader.String("text", string.Empty) ?? string.Empty,
            reader.Boolean("close"),
            cancellationToken);
    }

    private object ProcessResize(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        return processes.Resize(
            reader.RequireString("handle"),
            (short)Math.Clamp(reader.Int32("columns", 120), 20, 500),
            (short)Math.Clamp(reader.Int32("rows", 30), 5, 200));
    }

    private async Task<object> ProcessListAsync(
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        var local = processes.List();
        if (!runtime.IsService) return local;
        try
        {
            var user = await new SessionPipeClient(config.PipeName)
                .CallAsync("process.list", args, 2000, cancellationToken);
            return new { system = local, user = user.Ok ? user.Result : null, user_error = user.Error };
        }
        catch (Exception ex)
        {
            return new { system = local, user = (object?)null, user_error = new { message = ex.Message } };
        }
    }

    private async Task<object> SessionInfoAsync(
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        if (runtime.IsSession) return SessionIdentity.GetInfo();
        var timeoutMs = new ArgReader(args).Int32("timeout_ms", 2000);
        var response = await new SessionPipeClient(config.PipeName)
            .CallAsync("session.info", args, timeoutMs, cancellationToken);
        if (!response.Ok) throw new InvalidOperationException(response.Error?.Message ?? "Session operation failed.");
        return response.Result ?? new { };
    }
}
