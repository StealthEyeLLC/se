using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using StealthEye.Configuration;

namespace StealthEye.Runtime;

public sealed class ProcessRegistry
{
    private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new(StringComparer.Ordinal);
    private readonly EyeConfig _config;
    private readonly EyeRuntimeContext _runtime;

    public ProcessRegistry(EyeConfig config, EyeRuntimeContext runtime)
    {
        _config = config;
        _runtime = runtime;
    }

    public async Task<object> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var handle = _runtime.ProcessHandlePrefix + Guid.NewGuid().ToString("N");
        var directory = _config.ResolveProcessOutputDirectory(_runtime);
        Directory.CreateDirectory(directory);
        var record = new ManagedProcess(handle, spec, directory);
        if (!_processes.TryAdd(handle, record)) throw new InvalidOperationException("Failed to allocate process handle.");
        try
        {
            await record.StartAsync(cancellationToken);
            return record.Describe();
        }
        catch
        {
            _processes.TryRemove(handle, out _);
            await record.DisposeAsync();
            throw;
        }
    }

    public object List() => new
    {
        processes = _processes.Values.OrderBy(p => p.StartedAt).Select(p => p.Describe()).ToArray(),
    };

    public object Stat(string handle) => Get(handle).Describe();

    public async Task<object> ReadAsync(string handle, long stdoutOffset, long stderrOffset, int maxBytes, CancellationToken cancellationToken)
    {
        return await Get(handle).ReadAsync(stdoutOffset, stderrOffset, maxBytes, cancellationToken);
    }

    public async Task<object> WriteAsync(string handle, string text, bool close, CancellationToken cancellationToken)
    {
        await Get(handle).WriteAsync(text, close, cancellationToken);
        return new { handle, bytes_written = Encoding.UTF8.GetByteCount(text), stdin_closed = close };
    }

    public async Task<object> StopAsync(string handle, CancellationToken cancellationToken)
    {
        var process = Get(handle);
        await process.StopAsync(cancellationToken);
        return process.Describe();
    }

    public async Task<object> RemoveAsync(string handle)
    {
        if (!_processes.TryRemove(handle, out var process)) throw new KeyNotFoundException($"Unknown process handle '{handle}'.");
        var description = process.Describe();
        await process.DisposeAsync();
        return description;
    }

    private ManagedProcess Get(string handle) => _processes.TryGetValue(handle, out var process)
        ? process
        : throw new KeyNotFoundException($"Unknown process handle '{handle}'.");
}

internal sealed class ManagedProcess : IAsyncDisposable
{
    private readonly ProcessSpec _spec;
    private readonly string _stdoutPath;
    private readonly string _stderrPath;
    private Process? _process;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private readonly CancellationTokenSource _lifetime = new();

    public ManagedProcess(string handle, ProcessSpec spec, string directory)
    {
        Handle = handle;
        _spec = spec;
        _stdoutPath = Path.Combine(directory, handle + ".stdout");
        _stderrPath = Path.Combine(directory, handle + ".stderr");
    }

    public string Handle { get; }
    public DateTimeOffset StartedAt { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _process = new Process { StartInfo = _spec.ToStartInfo(), EnableRaisingEvents = true };
        StartedAt = DateTimeOffset.UtcNow;
        if (!_process.Start()) throw new InvalidOperationException($"Failed to start '{_spec.FileName}'.");
        _stdoutPump = PumpAsync(_process.StandardOutput.BaseStream, _stdoutPath, _lifetime.Token);
        _stderrPump = PumpAsync(_process.StandardError.BaseStream, _stderrPath, _lifetime.Token);
        if (_spec.StandardInput is not null)
        {
            await _process.StandardInput.WriteAsync(_spec.StandardInput.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
    }

    public object Describe()
    {
        var process = _process ?? throw new InvalidOperationException("Process has not started.");
        var exited = false;
        int? exitCode = null;
        try
        {
            exited = process.HasExited;
            if (exited) exitCode = process.ExitCode;
        }
        catch { }
        return new
        {
            handle = Handle,
            pid = process.Id,
            state = exited ? "exited" : "running",
            exit_code = exitCode,
            started_at = StartedAt,
            stdout_size = SafeLength(_stdoutPath),
            stderr_size = SafeLength(_stderrPath),
            file_name = _spec.FileName,
            context = _spec.RequestedContext,
        };
    }

    public async Task<object> ReadAsync(long stdoutOffset, long stderrOffset, int maxBytes, CancellationToken cancellationToken)
    {
        maxBytes = Math.Clamp(maxBytes, 1024, 8 * 1024 * 1024);
        var stdout = await ReadChunkAsync(_stdoutPath, Math.Max(0, stdoutOffset), maxBytes / 2, cancellationToken);
        var stderr = await ReadChunkAsync(_stderrPath, Math.Max(0, stderrOffset), maxBytes - stdout.Bytes.Length, cancellationToken);
        return new
        {
            handle = Handle,
            process = Describe(),
            stdout = Encoding.UTF8.GetString(stdout.Bytes),
            stdout_offset = stdoutOffset,
            stdout_next_offset = stdout.NextOffset,
            stdout_eof = stdout.Eof,
            stderr = Encoding.UTF8.GetString(stderr.Bytes),
            stderr_offset = stderrOffset,
            stderr_next_offset = stderr.NextOffset,
            stderr_eof = stderr.Eof,
        };
    }

    public async Task WriteAsync(string text, bool close, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Process has not started.");
        if (process.HasExited) throw new InvalidOperationException("Process has already exited.");
        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        if (close) process.StandardInput.Close();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Process has not started.");
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
        }
        if (_stdoutPump is not null) await _stdoutPump;
        if (_stderrPump is not null) await _stderrPump;
        _lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { await _process.WaitForExitAsync(); } catch { }
            if (_stdoutPump is not null) { try { await _stdoutPump; } catch { } }
            if (_stderrPump is not null) { try { await _stderrPump; } catch { } }
            _process.Dispose();
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static async Task PumpAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { await source.CopyToAsync(target, cancellationToken); }
        catch (OperationCanceledException) { }
        finally { await target.FlushAsync(CancellationToken.None); }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static async Task<(byte[] Bytes, long NextOffset, bool Eof)> ReadChunkAsync(string path, long offset, int length, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return ([], offset, true);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset > stream.Length) offset = stream.Length;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(length, (int)Math.Min(int.MaxValue, Math.Max(0, stream.Length - offset)))];
        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
        if (read != buffer.Length) Array.Resize(ref buffer, read);
        var next = offset + read;
        return (buffer, next, next >= stream.Length);
    }
}
