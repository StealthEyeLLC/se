using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StealthEye.Runtime;

namespace StealthEye.Operations;

public sealed class FileOperations
{
    public async Task<object> ReadAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var path = Path.GetFullPath(reader.RequireString("path"));
        var offset = Math.Max(0, reader.Int64("offset", 0));
        var length = Math.Clamp(reader.Int32("length", 1024 * 1024), 1, 16 * 1024 * 1024);
        var encoding = (reader.String("encoding", "utf8") ?? "utf8").ToLowerInvariant();

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset > stream.Length) offset = stream.Length;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[Math.Min(length, (int)Math.Min(int.MaxValue, stream.Length - offset))];
        var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
        if (read != buffer.Length) Array.Resize(ref buffer, read);
        var next = offset + read;
        return new
        {
            path,
            offset,
            next_offset = next,
            size = stream.Length,
            eof = next >= stream.Length,
            encoding,
            content = encoding == "base64" ? Convert.ToBase64String(buffer) : Encoding.UTF8.GetString(buffer),
        };
    }

    public async Task<object> WriteAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var path = Path.GetFullPath(reader.RequireString("path"));
        var append = reader.Boolean("append");
        var createDirectories = reader.Boolean("create_directories", true);
        var text = reader.String("content");
        var base64 = reader.String("content_base64");
        if (text is not null && base64 is not null) throw new ArgumentException("Use either 'content' or 'content_base64', not both.");
        if (text is null && base64 is null) throw new ArgumentException("One of 'content' or 'content_base64' is required.");
        var bytes = base64 is not null ? Convert.FromBase64String(base64) : Encoding.UTF8.GetBytes(text!);

        var parent = Path.GetDirectoryName(path);
        if (createDirectories && !string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        await using var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return new { path, bytes_written = bytes.Length, size = stream.Length, append };
    }

    public object List(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var path = Path.GetFullPath(reader.RequireString("path"));
        var recursive = reader.Boolean("recursive");
        var maxEntries = Math.Clamp(reader.Int32("max_entries", 1000), 1, 100_000);
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = Directory.EnumerateFileSystemEntries(path, "*", option)
            .Take(maxEntries)
            .Select(DescribePath)
            .ToArray();
        return new { path, recursive, entries, truncated = entries.Length == maxEntries };
    }

    public object Stat(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var path = Path.GetFullPath(new ArgReader(args).RequireString("path"));
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Path does not exist.", path);
        return DescribePath(path);
    }

    public object Mkdir(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var path = Path.GetFullPath(new ArgReader(args).RequireString("path"));
        Directory.CreateDirectory(path);
        return DescribePath(path);
    }

    public object Copy(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var source = Path.GetFullPath(reader.RequireString("source"));
        var destination = Path.GetFullPath(reader.RequireString("destination"));
        var overwrite = reader.Boolean("overwrite");
        if (Directory.Exists(source)) CopyDirectory(source, destination, overwrite);
        else File.Copy(source, destination, overwrite);
        return new { source, destination, overwrite };
    }

    public object Move(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var source = Path.GetFullPath(reader.RequireString("source"));
        var destination = Path.GetFullPath(reader.RequireString("destination"));
        var overwrite = reader.Boolean("overwrite");
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination, overwrite);
        return new { source, destination, overwrite };
    }

    public object Remove(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var path = Path.GetFullPath(reader.RequireString("path"));
        var recursive = reader.Boolean("recursive");
        if (Directory.Exists(path)) Directory.Delete(path, recursive);
        else if (File.Exists(path)) File.Delete(path);
        else throw new FileNotFoundException("Path does not exist.", path);
        return new { path, removed = true };
    }

    public async Task<object> HashAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(new ArgReader(args).RequireString("path"));
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new { path, algorithm = "sha256", hash = Convert.ToHexString(hash).ToLowerInvariant(), size = stream.Length };
    }

    private static object DescribePath(string path)
    {
        var attributes = File.GetAttributes(path);
        var directory = attributes.HasFlag(FileAttributes.Directory);
        if (directory)
        {
            var info = new DirectoryInfo(path);
            return new
            {
                path = info.FullName,
                name = info.Name,
                type = "directory",
                attributes = attributes.ToString(),
                created_at = info.CreationTimeUtc,
                modified_at = info.LastWriteTimeUtc,
            };
        }
        else
        {
            var info = new FileInfo(path);
            return new
            {
                path = info.FullName,
                name = info.Name,
                type = "file",
                size = info.Length,
                attributes = attributes.ToString(),
                created_at = info.CreationTimeUtc,
                modified_at = info.LastWriteTimeUtc,
            };
        }
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), overwrite);
        }
    }
}
