using System.Diagnostics;
using System.Text;
using StealthEye.Windows;

namespace StealthEye.Runtime;

public sealed class ProcessRunner
{
    public async Task<object> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        if (spec.Interactive) throw new ArgumentException("Interactive processes must use process.start or terminal.open.");
        using var process = new Process { StartInfo = spec.ToStartInfo(), EnableRaisingEvents = true };
        var startedAt = DateTimeOffset.UtcNow;
        if (!process.Start()) throw new InvalidOperationException($"Failed to start '{spec.FileName}'.");
        using var job = JobObject.TryAssign(process, out var jobError);

        if (spec.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = spec.TimeoutMs > 0 ? new CancellationTokenSource(spec.TimeoutMs) : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = Truncate(stdout, stderr, spec.MaxOutputBytes);
        var endedAt = DateTimeOffset.UtcNow;

        return new
        {
            pid = process.Id,
            exit_code = process.ExitCode,
            timed_out = timedOut,
            stdout = combined.Stdout,
            stderr = combined.Stderr,
            truncated = combined.Truncated,
            started_at = startedAt,
            ended_at = endedAt,
            duration_ms = (long)(endedAt - startedAt).TotalMilliseconds,
            context = spec.RequestedContext,
            effective_identity = GetIdentity(),
            job_assigned = job is not null,
            job_error = jobError,
        };
    }

    private static (string Stdout, string Stderr, bool Truncated) Truncate(string stdout, string stderr, int maxBytes)
    {
        var stdoutBytes = Encoding.UTF8.GetBytes(stdout);
        var stderrBytes = Encoding.UTF8.GetBytes(stderr);
        if (stdoutBytes.Length + stderrBytes.Length <= maxBytes) return (stdout, stderr, false);

        var stdoutBudget = Math.Min(stdoutBytes.Length, maxBytes * 3 / 4);
        var stderrBudget = Math.Min(stderrBytes.Length, maxBytes - stdoutBudget);
        if (stdoutBudget + stderrBudget < maxBytes && stdoutBudget < stdoutBytes.Length)
            stdoutBudget = Math.Min(stdoutBytes.Length, maxBytes - stderrBudget);

        return (
            Encoding.UTF8.GetString(stdoutBytes.AsSpan(0, stdoutBudget)),
            Encoding.UTF8.GetString(stderrBytes.AsSpan(0, stderrBudget)),
            true);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static string GetIdentity()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
        catch { return Environment.UserName; }
    }
}
