using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using StealthEye.Runtime;

namespace StealthEye.Windows;

public sealed class DesktopOperations
{
    private readonly SemaphoreSlim _inputGate = new(1, 1);

    static DesktopOperations()
    {
        try { _ = NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
    }

    public object Info()
    {
        var monitors = EnumerateDisplays();
        object? cursor = null;
        string? cursorError = null;
        try { cursor = CursorPosition(); }
        catch (Exception ex) { cursorError = ex.Message; }
        return new
        {
            interactive = Environment.UserInteractive,
            session_id = Process.GetCurrentProcess().SessionId,
            monitor_count = monitors.Length,
            virtual_bounds = VirtualBounds(),
            cursor,
            cursor_error = cursorError,
            foreground = DescribeWindow(NativeMethods.GetForegroundWindow()),
        };
    }

    public object ListDisplays()
    {
        var monitors = EnumerateDisplays();
        return new
        {
            monitors,
            count = monitors.Length,
            virtual_bounds = VirtualBounds(),
        };
    }

    public object ListWindows(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var visibleOnly = reader.Boolean("visible_only", true);
        var includeUntitled = reader.Boolean("include_untitled");
        var titleContains = reader.String("title_contains");
        var processId = reader.Int32("process_id", 0);
        var maxEntries = Math.Clamp(reader.Int32("max_entries", 500), 1, 5000);
        var foreground = NativeMethods.GetForegroundWindow();
        var windows = new List<object>();
        NativeMethods.EnumWindows((handle, data) =>
        {
            if (windows.Count >= maxEntries) return false;
            if (visibleOnly && !NativeMethods.IsWindowVisible(handle)) return true;
            _ = NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            if (processId > 0 && pid != processId) return true;
            var title = GetWindowText(handle);
            if (!includeUntitled && string.IsNullOrWhiteSpace(title)) return true;
            if (!string.IsNullOrWhiteSpace(titleContains)
                && !title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)) return true;
            windows.Add(DescribeWindow(handle, foreground));
            return true;
        }, IntPtr.Zero);

        return new
        {
            windows,
            count = windows.Count,
            truncated = windows.Count >= maxEntries,
        };
    }

    public object ForegroundWindow() => DescribeWindow(NativeMethods.GetForegroundWindow());

    public object ActivateWindow(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var handle = ResolveWindow(args);
        if (NativeMethods.IsIconic(handle)) _ = NativeMethods.ShowWindowAsync(handle, NativeMethods.SwRestore);

        var foreground = NativeMethods.GetForegroundWindow();
        var currentThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(handle, out _);
        var foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attachedTarget = targetThread != 0 && targetThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread
            && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            _ = NativeMethods.BringWindowToTop(handle);
            _ = NativeMethods.SetForegroundWindow(handle);
            _ = NativeMethods.SetActiveWindow(handle);
        }
        finally
        {
            if (attachedForeground) _ = NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            if (attachedTarget) _ = NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }

        return new
        {
            activated = NativeMethods.GetForegroundWindow() == handle,
            window = DescribeWindow(handle),
        };
    }

    public object MoveWindow(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var handle = ResolveWindow(args);
        if (!NativeMethods.GetWindowRect(handle, out var rect)) ThrowLastWin32("GetWindowRect");
        var x = reader.Has("x") ? reader.Int32("x") : rect.Left;
        var y = reader.Has("y") ? reader.Int32("y") : rect.Top;
        var width = reader.Has("width") ? Math.Max(1, reader.Int32("width")) : rect.Width;
        var height = reader.Has("height") ? Math.Max(1, reader.Int32("height")) : rect.Height;
        if (!NativeMethods.SetWindowPos(
                handle,
                IntPtr.Zero,
                x,
                y,
                width,
                height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder)) ThrowLastWin32("SetWindowPos");
        return DescribeWindow(handle);
    }

    public object ShowWindow(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var handle = ResolveWindow(args);
        var state = (reader.String("state", "restore") ?? "restore").ToLowerInvariant();
        var command = state switch
        {
            "hide" => NativeMethods.SwHide,
            "show" or "normal" => NativeMethods.SwShow,
            "minimize" or "minimized" => NativeMethods.SwMinimize,
            "maximize" or "maximized" => NativeMethods.SwMaximize,
            "restore" => NativeMethods.SwRestore,
            _ => throw new ArgumentException("'state' must be hide, show, minimize, maximize, or restore."),
        };
        _ = NativeMethods.ShowWindowAsync(handle, command);
        return new { state, window = DescribeWindow(handle) };
    }

    public object PointerPosition() => CursorPosition();

    public async Task<object> MovePointerAsync(
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            var current = GetCursorPosition();
            var x = reader.Has("x") ? reader.Int32("x") : current.X + reader.Int32("dx");
            var y = reader.Has("y") ? reader.Int32("y") : current.Y + reader.Int32("dy");
            if (!NativeMethods.SetCursorPos(x, y)) ThrowLastWin32("SetCursorPos");
            return CursorPosition();
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public async Task<object> ClickPointerAsync(
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var button = (reader.String("button", "left") ?? "left").ToLowerInvariant();
        var clicks = Math.Clamp(reader.Int32("clicks", 1), 1, 10);
        var intervalMs = Math.Clamp(reader.Int32("interval_ms", 60), 0, 5000);
        var (down, up) = button switch
        {
            "left" => (NativeMethods.MouseeventfLeftdown, NativeMethods.MouseeventfLeftup),
            "right" => (NativeMethods.MouseeventfRightdown, NativeMethods.MouseeventfRightup),
            "middle" => (NativeMethods.MouseeventfMiddledown, NativeMethods.MouseeventfMiddleup),
            _ => throw new ArgumentException("'button' must be left, right, or middle."),
        };

        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            if (reader.Has("x") || reader.Has("y"))
            {
                var current = GetCursorPosition();
                var x = reader.Has("x") ? reader.Int32("x") : current.X;
                var y = reader.Has("y") ? reader.Int32("y") : current.Y;
                if (!NativeMethods.SetCursorPos(x, y)) ThrowLastWin32("SetCursorPos");
            }
            for (var i = 0; i < clicks; i++)
            {
                SendMouse(down);
                SendMouse(up);
                if (intervalMs > 0 && i + 1 < clicks) await Task.Delay(intervalMs, cancellationToken);
            }
            return new { button, clicks, position = CursorPosition() };
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public async Task<object> ScrollPointerAsync(
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var delta = reader.Int32("delta", 120);
        var horizontal = reader.Boolean("horizontal");
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            if (reader.Has("x") || reader.Has("y"))
            {
                var current = GetCursorPosition();
                if (!NativeMethods.SetCursorPos(
                        reader.Has("x") ? reader.Int32("x") : current.X,
                        reader.Has("y") ? reader.Int32("y") : current.Y)) ThrowLastWin32("SetCursorPos");
            }
            SendMouse(horizontal ? NativeMethods.MouseeventfHwheel : NativeMethods.MouseeventfWheel, unchecked((uint)delta));
            return new { delta, horizontal, position = CursorPosition() };
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public async Task<object> TypeTextAsync(
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var text = reader.String("text") ?? throw new ArgumentException("Missing required string argument 'text'.");
        var intervalMs = Math.Clamp(reader.Int32("interval_ms", 0), 0, 5000);
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                if (character == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') continue;
                    SendVirtualKey(NativeMethods.VkReturn, false);
                    SendVirtualKey(NativeMethods.VkReturn, true);
                }
                else if (character == '\n')
                {
                    SendVirtualKey(NativeMethods.VkReturn, false);
                    SendVirtualKey(NativeMethods.VkReturn, true);
                }
                else if (character == '\t')
                {
                    SendVirtualKey(NativeMethods.VkTab, false);
                    SendVirtualKey(NativeMethods.VkTab, true);
                }
                else
                {
                    SendUnicode(character, false);
                    SendUnicode(character, true);
                }
                if (intervalMs > 0 && i + 1 < text.Length) await Task.Delay(intervalMs, cancellationToken);
            }
            return new { characters = text.Length };
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public async Task<object> SendKeysAsync(
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var keys = reader.Strings("keys").ToList();
        if (keys.Count == 0 && !string.IsNullOrWhiteSpace(reader.String("key"))) keys.Add(reader.String("key")!);
        if (keys.Count == 0) throw new ArgumentException("'key' or 'keys' is required.");
        var virtualKeys = keys.Select(ParseVirtualKey).ToArray();
        var action = (reader.String("action", "press") ?? "press").ToLowerInvariant();
        var repeat = Math.Clamp(reader.Int32("repeat", 1), 1, 100);
        var intervalMs = Math.Clamp(reader.Int32("interval_ms", 50), 0, 5000);

        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            for (var repetition = 0; repetition < repeat; repetition++)
            {
                switch (action)
                {
                    case "press":
                        foreach (var key in virtualKeys) SendVirtualKey(key, false);
                        foreach (var key in virtualKeys.Reverse()) SendVirtualKey(key, true);
                        break;
                    case "down":
                        foreach (var key in virtualKeys) SendVirtualKey(key, false);
                        break;
                    case "up":
                        foreach (var key in virtualKeys.Reverse()) SendVirtualKey(key, true);
                        break;
                    default:
                        throw new ArgumentException("'action' must be press, down, or up.");
                }
                if (intervalMs > 0 && repetition + 1 < repeat) await Task.Delay(intervalMs, cancellationToken);
            }
            return new { keys, action, repeat };
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public object ReadClipboard()
    {
        OpenClipboardWithRetry();
        try
        {
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfUnicodeText))
                return new { text = (string?)null, format_available = false, sequence };
            var handle = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
            if (handle == IntPtr.Zero) ThrowLastWin32("GetClipboardData");
            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero) ThrowLastWin32("GlobalLock");
            try
            {
                return new
                {
                    text = Marshal.PtrToStringUni(pointer) ?? string.Empty,
                    format_available = true,
                    sequence,
                };
            }
            finally
            {
                _ = NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = NativeMethods.CloseClipboard();
        }
    }

    public async Task<object> WriteClipboardAsync(
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        var text = new ArgReader(args).String("text")
            ?? throw new ArgumentException("Missing required string argument 'text'.");
        await _inputGate.WaitAsync(cancellationToken);
        try
        {
            OpenClipboardWithRetry();
            IntPtr memory = IntPtr.Zero;
            try
            {
                if (!NativeMethods.EmptyClipboard()) ThrowLastWin32("EmptyClipboard");
                var bytes = Encoding.Unicode.GetBytes(text + '\0');
                memory = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (nuint)bytes.Length);
                if (memory == IntPtr.Zero) ThrowLastWin32("GlobalAlloc");
                var pointer = NativeMethods.GlobalLock(memory);
                if (pointer == IntPtr.Zero) ThrowLastWin32("GlobalLock");
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { _ = NativeMethods.GlobalUnlock(memory); }
                if (NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, memory) == IntPtr.Zero)
                    ThrowLastWin32("SetClipboardData");
                memory = IntPtr.Zero;
                return new { characters = text.Length, sequence = NativeMethods.GetClipboardSequenceNumber() };
            }
            finally
            {
                if (memory != IntPtr.Zero) _ = NativeMethods.GlobalFree(memory);
                _ = NativeMethods.CloseClipboard();
            }
        }
        finally
        {
            _inputGate.Release();
        }
    }

    private static object[] EnumerateDisplays()
    {
        var displays = new List<object>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return true;
            displays.Add(new
            {
                handle = FormatHandle(monitor),
                device_name = info.DeviceName,
                primary = (info.Flags & NativeMethods.MonitorinfofPrimary) != 0,
                bounds = RectValue(info.Monitor),
                work_area = RectValue(info.WorkArea),
            });
            return true;
        }, IntPtr.Zero);
        return displays.ToArray();
    }

    private static object DescribeWindow(IntPtr handle, IntPtr? foregroundOverride = null)
    {
        if (handle == IntPtr.Zero) return new { available = false, handle = (string?)null };
        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        NativeMethods.GetWindowRect(handle, out var rect);
        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassName(handle, className, className.Capacity);
        string? processName = null;
        try { processName = Process.GetProcessById((int)processId).ProcessName; } catch { }
        return new
        {
            handle = FormatHandle(handle),
            title = GetWindowText(handle),
            class_name = className.ToString(),
            process_id = processId,
            process_name = processName,
            visible = NativeMethods.IsWindowVisible(handle),
            minimized = NativeMethods.IsIconic(handle),
            maximized = NativeMethods.IsZoomed(handle),
            foreground = (foregroundOverride ?? NativeMethods.GetForegroundWindow()) == handle,
            rect = RectValue(rect),
        };
    }

    private static IntPtr ResolveWindow(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var handleText = reader.String("handle");
        if (!string.IsNullOrWhiteSpace(handleText)) return ParseHandle(handleText);
        var processId = reader.Int32("process_id", 0);
        var title = reader.String("title_contains");
        if (processId <= 0 && string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Provide 'handle', 'process_id', or 'title_contains'.");

        var matches = new List<IntPtr>();
        NativeMethods.EnumWindows((handle, data) =>
        {
            if (!NativeMethods.IsWindowVisible(handle)) return true;
            _ = NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            if (processId > 0 && pid != processId) return true;
            if (!string.IsNullOrWhiteSpace(title)
                && !GetWindowText(handle).Contains(title, StringComparison.OrdinalIgnoreCase)) return true;
            matches.Add(handle);
            return true;
        }, IntPtr.Zero);
        if (matches.Count == 0) throw new ArgumentException("No matching top-level window was found.");
        var foreground = NativeMethods.GetForegroundWindow();
        return matches.Contains(foreground) ? foreground : matches[0];
    }

    private static IntPtr ParseHandle(string value)
    {
        var text = value.Trim();
        var style = NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = NumberStyles.HexNumber;
        }
        if (!long.TryParse(text, style, CultureInfo.InvariantCulture, out var result) || result == 0)
            throw new ArgumentException("Window handle must be a nonzero decimal or 0x-prefixed hexadecimal value.");
        var handle = new IntPtr(result);
        if (!NativeMethods.IsWindow(handle)) throw new ArgumentException("Window handle is not valid.");
        return handle;
    }

    private static string GetWindowText(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var text = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(handle, text, text.Capacity);
        return text.ToString();
    }

    private static object CursorPosition()
    {
        var point = GetCursorPosition();
        return new { x = point.X, y = point.Y };
    }

    private static Point GetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out var point)) ThrowLastWin32("GetCursorPos");
        return point;
    }

    private static object VirtualBounds() => new
    {
        x = NativeMethods.GetSystemMetrics(NativeMethods.SmXvirtualscreen),
        y = NativeMethods.GetSystemMetrics(NativeMethods.SmYvirtualscreen),
        width = NativeMethods.GetSystemMetrics(NativeMethods.SmCxvirtualscreen),
        height = NativeMethods.GetSystemMetrics(NativeMethods.SmCyvirtualscreen),
    };

    private static object RectValue(Rect rect) => new
    {
        x = rect.Left,
        y = rect.Top,
        width = rect.Width,
        height = rect.Height,
        left = rect.Left,
        top = rect.Top,
        right = rect.Right,
        bottom = rect.Bottom,
    };

    private static string FormatHandle(IntPtr handle) => $"0x{handle.ToInt64():X}";

    private static void SendMouse(uint flags, uint data = 0)
    {
        var input = new Input
        {
            Type = NativeMethods.InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInput { Flags = flags, MouseData = data },
            },
        };
        SendInputs([input]);
    }

    private static void SendUnicode(char character, bool keyUp)
    {
        var input = new Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    Scan = character,
                    Flags = NativeMethods.KeyeventfUnicode | (keyUp ? NativeMethods.KeyeventfKeyup : 0),
                },
            },
        };
        SendInputs([input]);
    }

    private static void SendVirtualKey(ushort virtualKey, bool keyUp)
    {
        var extended = IsExtendedKey(virtualKey) ? NativeMethods.KeyeventfExtendedkey : 0u;
        var input = new Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Scan = (ushort)NativeMethods.MapVirtualKey(virtualKey, 0),
                    Flags = extended | (keyUp ? NativeMethods.KeyeventfKeyup : 0),
                },
            },
        };
        SendInputs([input]);
    }

    private static void SendInputs(Input[] inputs)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length) ThrowLastWin32("SendInput");
    }

    private static ushort ParseVirtualKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Key names cannot be empty.");
        var normalized = name.Trim().Replace("-", string.Empty).Replace("_", string.Empty).ToUpperInvariant();
        if (normalized.Length == 1)
        {
            var character = normalized[0];
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9') return character;
        }
        if (normalized.StartsWith('F') && int.TryParse(normalized[1..], out var function) && function is >= 1 and <= 24)
            return (ushort)(0x70 + function - 1);
        return normalized switch
        {
            "CTRL" or "CONTROL" => 0x11,
            "SHIFT" => 0x10,
            "ALT" or "MENU" => 0x12,
            "WIN" or "WINDOWS" or "LWIN" => 0x5B,
            "RWIN" => 0x5C,
            "ENTER" or "RETURN" => NativeMethods.VkReturn,
            "TAB" => NativeMethods.VkTab,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "BACKSPACE" or "BACK" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "CAPSLOCK" => 0x14,
            "NUMLOCK" => 0x90,
            "SCROLLLOCK" => 0x91,
            "PRINTSCREEN" or "PRTSC" => 0x2C,
            "PAUSE" => 0x13,
            _ => throw new ArgumentException($"Unsupported key '{name}'."),
        };
    }

    private static bool IsExtendedKey(ushort key) => key is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C;

    private static void OpenClipboardWithRetry()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (NativeMethods.OpenClipboard(IntPtr.Zero)) return;
            Thread.Sleep(25);
        }
        ThrowLastWin32("OpenClipboard");
    }

    private static void ThrowLastWin32(string operation) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed");

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParamL;
        public ushort ParamH;
    }

    private static class NativeMethods
    {
        internal const uint InputMouse = 0;
        internal const uint InputKeyboard = 1;
        internal const uint MouseeventfLeftdown = 0x0002;
        internal const uint MouseeventfLeftup = 0x0004;
        internal const uint MouseeventfRightdown = 0x0008;
        internal const uint MouseeventfRightup = 0x0010;
        internal const uint MouseeventfMiddledown = 0x0020;
        internal const uint MouseeventfMiddleup = 0x0040;
        internal const uint MouseeventfWheel = 0x0800;
        internal const uint MouseeventfHwheel = 0x1000;
        internal const uint KeyeventfExtendedkey = 0x0001;
        internal const uint KeyeventfKeyup = 0x0002;
        internal const uint KeyeventfUnicode = 0x0004;
        internal const ushort VkTab = 0x09;
        internal const ushort VkReturn = 0x0D;
        internal const int SwHide = 0;
        internal const int SwShow = 5;
        internal const int SwMinimize = 6;
        internal const int SwRestore = 9;
        internal const int SwMaximize = 3;
        internal const uint SwpNoZOrder = 0x0004;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint MonitorinfofPrimary = 0x00000001;
        internal const int SmXvirtualscreen = 76;
        internal const int SmYvirtualscreen = 77;
        internal const int SmCxvirtualscreen = 78;
        internal const int SmCyvirtualscreen = 79;
        internal const uint CfUnicodeText = 13;
        internal const uint GmemMoveable = 0x0002;

        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr data);
        internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetActiveWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll")]
        internal static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DllImport("user32.dll")]
        internal static extern uint GetClipboardSequenceNumber();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalLock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalFree(IntPtr memory);
    }
}
