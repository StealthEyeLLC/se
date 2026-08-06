using System.Diagnostics;
using Pty.Net;
using StealthEye.Runtime;

namespace StealthEye.Windows;

internal sealed class ConPtySession : IAsyncDisposable
{
    private readonly IPtyConnection _connection;
    private bool _disposed;

    private ConPtySession(IPtyConnection connection, Process process)
    {
        _connection = connection;
        Process = process;
    }

    public Process Process { get; }
    public Stream Input => _connection.WriterStream;
    public Stream Output => _connection.ReaderStream;

    public static async Task<ConPtySession> StartAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var options = new PtyOptions
        {
            Name = "StealthEye " + Path.GetFileNameWithoutExtension(spec.FileName),
            Rows = spec.Rows,
            Cols = spec.Columns,
            Cwd = string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? Environment.CurrentDirectory : spec.WorkingDirectory,
            App = spec.FileName,
            CommandLine = spec.Arguments.ToArray(),
            Environment = new Dictionary<string, string>(spec.Environment, StringComparer.OrdinalIgnoreCase),
        };
        var connection = await PtyProvider.SpawnAsync(options, cancellationToken);
        var process = Process.GetProcessById(connection.Pid);
        return new ConPtySession(connection, process);
    }

    public void Resize(short columns, short rows) => _connection.Resize(columns, rows);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _connection.Dispose();
        Process.Dispose();
        return ValueTask.CompletedTask;
    }
}
