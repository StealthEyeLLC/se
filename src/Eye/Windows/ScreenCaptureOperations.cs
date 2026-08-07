using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using StealthEye.Runtime;

namespace StealthEye.Windows;

public sealed class ScreenCaptureOperations
{
    public ScreenCaptureResult Capture(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var target = (reader.String("target", "desktop") ?? "desktop").ToLowerInvariant();
        var format = (reader.String("format", "png") ?? "png").ToLowerInvariant();
        var source = ResolveSource(target, args, reader);
        using var captured = CaptureSource(source);
        using var output = ResizeIfNeeded(captured, reader.Int32("max_width"), reader.Int32("max_height"));
        var bytes = Encode(output, format, Math.Clamp(reader.Int32("quality", 90), 1, 100), out var mimeType, out var extension);

        string? savedPath = null;
        var requestedPath = reader.String("path");
        if (!string.IsNullOrWhiteSpace(requestedPath) || reader.Boolean("save"))
        {
            savedPath = string.IsNullOrWhiteSpace(requestedPath)
                ? DefaultCapturePath(extension)
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath));
            Directory.CreateDirectory(Path.GetDirectoryName(savedPath)!);
            File.WriteAllBytes(savedPath, bytes);
        }

        return new ScreenCaptureResult(
            bytes,
            mimeType,
            output.Width,
            output.Height,
            source.Target,
            source.Backend,
            source.Bounds.Left,
            source.Bounds.Top,
            source.Bounds.Width,
            source.Bounds.Height,
            savedPath,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static CaptureTarget ResolveSource(
        string target,
        IReadOnlyDictionary<string, JsonElement>? args,
        ArgReader reader)
    {
        return target switch
        {
            "desktop" or "virtual" => new CaptureTarget(
                "desktop",
                new Rectangle(
                    NativeMethods.GetSystemMetrics(NativeMethods.SmXvirtualscreen),
                    NativeMethods.GetSystemMetrics(NativeMethods.SmYvirtualscreen),
                    NativeMethods.GetSystemMetrics(NativeMethods.SmCxvirtualscreen),
                    NativeMethods.GetSystemMetrics(NativeMethods.SmCyvirtualscreen)),
                IntPtr.Zero,
                "gdi_copy"),
            "region" => ResolveRegion(reader),
            "window" => ResolveWindow(args, reader),
            _ => throw new ArgumentException("'target' must be desktop, region, or window."),
        };
    }

    private static CaptureTarget ResolveRegion(ArgReader reader)
    {
        var width = reader.Int32("width");
        var height = reader.Int32("height");
        if (width <= 0 || height <= 0) throw new ArgumentException("Region capture requires positive 'width' and 'height'.");
        return new CaptureTarget(
            "region",
            new Rectangle(reader.Int32("x"), reader.Int32("y"), width, height),
            IntPtr.Zero,
            "gdi_copy");
    }

    private static CaptureTarget ResolveWindow(IReadOnlyDictionary<string, JsonElement>? args, ArgReader reader)
    {
        var handle = DesktopOperations.ResolveWindowHandle(args);
        if (!NativeMethods.GetWindowRect(handle, out var rect)) ThrowLastWin32("GetWindowRect");
        if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException("Window has no capturable area.");
        var includeShadow = reader.Boolean("include_shadow", true);
        if (includeShadow && NativeMethods.DwmGetWindowAttribute(
                handle,
                NativeMethods.DwmwaExtendedFrameBounds,
                out var frame,
                Marshal.SizeOf<NativeRect>()) == 0
            && frame.Width > 0 && frame.Height > 0)
        {
            rect = frame;
        }
        return new CaptureTarget("window", new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height), handle, "print_window_or_gdi");
    }

    private static Bitmap CaptureSource(CaptureTarget source)
    {
        if (source.Bounds.Width <= 0 || source.Bounds.Height <= 0) throw new InvalidOperationException("Capture bounds are empty.");
        var bitmap = new Bitmap(source.Bounds.Width, source.Bounds.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);

        if (source.Window != IntPtr.Zero)
        {
            var hdc = graphics.GetHdc();
            bool printed;
            try { printed = NativeMethods.PrintWindow(source.Window, hdc, NativeMethods.PwRenderFullContent); }
            finally { graphics.ReleaseHdc(hdc); }
            if (printed) return bitmap;
        }

        graphics.CopyFromScreen(
            source.Bounds.Left,
            source.Bounds.Top,
            0,
            0,
            source.Bounds.Size,
            CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static Bitmap ResizeIfNeeded(Bitmap source, int maxWidth, int maxHeight)
    {
        maxWidth = maxWidth <= 0 ? source.Width : maxWidth;
        maxHeight = maxHeight <= 0 ? source.Height : maxHeight;
        var scale = Math.Min(1d, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        if (scale >= 0.999999) return new Bitmap(source);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(resized);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return resized;
    }

    private static byte[] Encode(Bitmap bitmap, string format, int quality, out string mimeType, out string extension)
    {
        using var stream = new MemoryStream();
        switch (format)
        {
            case "png":
                mimeType = "image/png";
                extension = ".png";
                bitmap.Save(stream, ImageFormat.Png);
                break;
            case "jpg":
            case "jpeg":
                mimeType = "image/jpeg";
                extension = ".jpg";
                var codec = ImageCodecInfo.GetImageEncoders().First(item => item.FormatID == ImageFormat.Jpeg.Guid);
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                    bitmap.Save(stream, codec, parameters);
                }
                break;
            default:
                throw new ArgumentException("'format' must be png, jpg, or jpeg.");
        }
        return stream.ToArray();
    }

    private static string DefaultCapturePath(string extension)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StealthEye",
            "captures");
        return Path.Combine(root, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff") + extension);
    }

    private static void ThrowLastWin32(string operation) =>
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), operation + " failed");

    private sealed record CaptureTarget(string Target, Rectangle Bounds, IntPtr Window, string Backend);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private static class NativeMethods
    {
        internal const int SmXvirtualscreen = 76;
        internal const int SmYvirtualscreen = 77;
        internal const int SmCxvirtualscreen = 78;
        internal const int SmCyvirtualscreen = 79;
        internal const uint PwRenderFullContent = 0x00000002;
        internal const int DwmwaExtendedFrameBounds = 9;

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
    }
}

public sealed record ScreenCaptureResult(
    [property: System.Text.Json.Serialization.JsonIgnore] byte[] ImageBytes,
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
    string Sha256)
{
    public int Bytes => ImageBytes.Length;
}
