using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;
using StealthEye.Runtime;

namespace StealthEye.Windows;

public sealed class BrowserOperations : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _playwrightGate = new(1, 1);
    private IPlaywright? _playwright;

    public async Task<object> StartAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var profile = NormalizeProfile(reader.String("profile", "default") ?? "default");
        var engine = NormalizeEngine(reader.String("engine", "chrome") ?? "chrome");
        var session = await GetOrStartAsync(profile, engine, args, cancellationToken);
        return await DescribeSessionAsync(session, cancellationToken);
    }

    public Task<object> ListAsync(CancellationToken cancellationToken)
    {
        var profiles = new List<object>();
        var root = ProfilesRoot();
        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profile = Path.GetFileName(directory);
                var state = ReadState(profile);
                if (state is null) continue;
                profiles.Add(new
                {
                    profile,
                    state.engine,
                    state.pid,
                    running = IsProcessRunning(state.pid),
                    state.started_at,
                    user_data_dir = directory,
                    devtools_port = TryReadDevToolsPort(directory),
                });
            }
        }
        return Task.FromResult<object>(new { profiles });
    }

    public async Task<object> StopAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var profile = NormalizeProfile(new ArgReader(args).String("profile", "default") ?? "default");
        var gate = _profileGates.GetOrAdd(profile, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            _sessions.TryRemove(profile, out var existing);
            var state = existing?.State ?? ReadState(profile);
            if (state is null) return new { profile, stopped = false, reason = "not_running" };
            try
            {
                using var process = Process.GetProcessById(state.pid);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            DeleteState(profile);
            return new { profile, stopped = true, pid = state.pid };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<object> TabsAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(args, cancellationToken);
        var context = RequireContext(session);
        var tabs = new List<object>();
        for (var i = 0; i < context.Pages.Count; i++)
        {
            var page = context.Pages[i];
            string title;
            try { title = await page.TitleAsync(); } catch { title = string.Empty; }
            tabs.Add(new { index = i, url = page.Url, title, closed = page.IsClosed });
        }
        return new { profile = session.Profile, tabs, count = tabs.Count };
    }

    public async Task<object> NewTabAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await RequireContext(session).NewPageAsync();
        var reader = new ArgReader(args);
        var url = reader.String("url");
        if (!string.IsNullOrWhiteSpace(url)) await NavigatePageAsync(page, url, reader, cancellationToken);
        return await DescribePageAsync(session, page, cancellationToken);
    }

    public async Task<object> CloseTabAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        var before = await DescribePageAsync(session, page, cancellationToken);
        await page.CloseAsync();
        return new { closed = true, page = before };
    }

    public async Task<object> NavigateAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var url = reader.RequireString("url");
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        var response = await NavigatePageAsync(page, url, reader, cancellationToken);
        return new
        {
            page = await DescribePageAsync(session, page, cancellationToken),
            response = response is null ? null : new { status = response.Status, ok = response.Ok, url = response.Url },
        };
    }

    public async Task<object> ClickAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var selector = reader.RequireString("selector");
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        await page.Locator(selector).ClickAsync(new LocatorClickOptions
        {
            Timeout = Timeout(reader),
            Force = reader.Boolean("force"),
            ClickCount = Math.Clamp(reader.Int32("clicks", 1), 1, 10),
        });
        return new { clicked = true, selector, page = await DescribePageAsync(session, page, cancellationToken) };
    }

    public async Task<object> FillAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var selector = reader.RequireString("selector");
        var value = reader.String("value", string.Empty) ?? string.Empty;
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        await page.Locator(selector).FillAsync(value, new LocatorFillOptions { Timeout = Timeout(reader) });
        return new { filled = true, selector, characters = value.Length };
    }

    public async Task<object> PressAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var key = reader.RequireString("key");
        var selector = reader.String("selector", "body") ?? "body";
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        await page.Locator(selector).PressAsync(key, new LocatorPressOptions { Timeout = Timeout(reader) });
        return new { pressed = true, selector, key };
    }

    public async Task<object> SnapshotAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        var mode = (reader.String("mode", "text") ?? "text").ToLowerInvariant();
        var maxChars = Math.Clamp(reader.Int32("max_chars", 200_000), 1024, 4_000_000);
        string content;
        if (mode == "html")
        {
            content = await page.ContentAsync();
        }
        else if (mode == "text")
        {
            var selector = reader.String("selector", "body") ?? "body";
            content = await page.Locator(selector).InnerTextAsync(new LocatorInnerTextOptions { Timeout = Timeout(reader) });
        }
        else
        {
            throw new ArgumentException("'mode' must be text or html.");
        }
        var truncated = content.Length > maxChars;
        if (truncated) content = content[..maxChars];
        return new
        {
            page = await DescribePageAsync(session, page, cancellationToken),
            mode,
            content,
            truncated,
            characters = content.Length,
        };
    }

    public async Task<object> EvaluateAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var expression = reader.RequireString("expression");
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        object? argument = null;
        if (args is not null && args.TryGetValue("arg", out var argumentElement))
            argument = JsonSerializer.Deserialize<object>(argumentElement.GetRawText());
        var value = await page.EvaluateAsync<JsonElement?>(expression, argument);
        return new { value };
    }

    public async Task<object> WaitAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var selector = reader.RequireString("selector");
        var stateText = (reader.String("state", "visible") ?? "visible").ToLowerInvariant();
        var state = stateText switch
        {
            "attached" => WaitForSelectorState.Attached,
            "detached" => WaitForSelectorState.Detached,
            "visible" => WaitForSelectorState.Visible,
            "hidden" => WaitForSelectorState.Hidden,
            _ => throw new ArgumentException("'state' must be attached, detached, visible, or hidden."),
        };
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        await page.Locator(selector).WaitForAsync(new LocatorWaitForOptions { State = state, Timeout = Timeout(reader) });
        return new { selector, state = stateText, satisfied = true };
    }

    public async Task<object> UploadAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var selector = reader.RequireString("selector");
        var files = reader.Strings("paths").Select(Path.GetFullPath).ToArray();
        if (files.Length == 0 && !string.IsNullOrWhiteSpace(reader.String("path"))) files = [Path.GetFullPath(reader.String("path")!)];
        if (files.Length == 0) throw new ArgumentException("'path' or 'paths' is required.");
        foreach (var file in files) if (!File.Exists(file)) throw new ArgumentException($"Upload file does not exist: {file}");
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        await page.Locator(selector).SetInputFilesAsync(files);
        return new { selector, files };
    }

    public async Task<object> DownloadAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var selector = reader.RequireString("selector");
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        var download = await page.RunAndWaitForDownloadAsync(
            async () => await page.Locator(selector).ClickAsync(new LocatorClickOptions { Timeout = Timeout(reader) }),
            new PageRunAndWaitForDownloadOptions { Timeout = Timeout(reader) });
        var requested = reader.String("path");
        var directory = reader.String("directory");
        string target;
        if (!string.IsNullOrWhiteSpace(requested)) target = Path.GetFullPath(requested);
        else
        {
            var root = string.IsNullOrWhiteSpace(directory) ? DefaultDownloadsRoot() : Path.GetFullPath(directory);
            Directory.CreateDirectory(root);
            target = UniquePath(Path.Combine(root, download.SuggestedFilename));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await download.SaveAsAsync(target);
        return new
        {
            path = target,
            suggested_filename = download.SuggestedFilename,
            failure = await download.FailureAsync(),
        };
    }

    public async Task<ScreenCaptureResult> ScreenshotAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        var format = (reader.String("format", "png") ?? "png").ToLowerInvariant();
        if (format is not ("png" or "jpg" or "jpeg")) throw new ArgumentException("'format' must be png, jpg, or jpeg.");
        var type = format == "png" ? ScreenshotType.Png : ScreenshotType.Jpeg;
        var quality = type == ScreenshotType.Jpeg ? Math.Clamp(reader.Int32("quality", 90), 1, 100) : (int?)null;
        var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
        {
            FullPage = reader.Boolean("full_page"),
            Type = type,
            Quality = quality,
        });
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        string? savedPath = null;
        var requested = reader.String("path");
        if (!string.IsNullOrWhiteSpace(requested) || reader.Boolean("save"))
        {
            var extension = type == ScreenshotType.Png ? ".png" : ".jpg";
            savedPath = string.IsNullOrWhiteSpace(requested)
                ? Path.Combine(DefaultScreenshotsRoot(), DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff") + extension)
                : Path.GetFullPath(requested);
            Directory.CreateDirectory(Path.GetDirectoryName(savedPath)!);
            await File.WriteAllBytesAsync(savedPath, bytes, cancellationToken);
        }
        return new ScreenCaptureResult(
            bytes,
            type == ScreenshotType.Png ? "image/png" : "image/jpeg",
            image.Width,
            image.Height,
            "browser",
            "playwright_cdp",
            0,
            0,
            image.Width,
            image.Height,
            savedPath,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public async Task<object> CdpAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var method = reader.RequireString("method");
        var session = await RequireSessionAsync(args, cancellationToken);
        var page = await ResolvePageAsync(session, args, cancellationToken);
        var cdp = await RequireContext(session).NewCDPSessionAsync(page);
        try
        {
            Dictionary<string, object>? parameters = null;
            if (args is not null && args.TryGetValue("params", out var paramsElement) && paramsElement.ValueKind == JsonValueKind.Object)
                parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(paramsElement.GetRawText());
            var result = await cdp.SendAsync(method, parameters);
            return new { method, result };
        }
        finally
        {
            await cdp.DetachAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var gate in _profileGates.Values) gate.Dispose();
        _profileGates.Clear();
        _sessions.Clear();
        _playwright?.Dispose();
        _playwright = null;
        _playwrightGate.Dispose();
        await Task.CompletedTask;
    }

    private async Task<BrowserSession> RequireSessionAsync(IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var reader = new ArgReader(args);
        var profile = NormalizeProfile(reader.String("profile", "default") ?? "default");
        var engine = NormalizeEngine(reader.String("engine", ReadState(profile)?.engine ?? "chrome") ?? "chrome");
        return await GetOrStartAsync(profile, engine, args, cancellationToken);
    }

    private async Task<BrowserSession> GetOrStartAsync(
        string profile,
        string engine,
        IReadOnlyDictionary<string, JsonElement>? args,
        CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(profile, out var cached) && cached.Browser.IsConnected) return cached;
        var gate = _profileGates.GetOrAdd(profile, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(profile, out cached) && cached.Browser.IsConnected) return cached;
            var directory = ProfileDirectory(profile);
            Directory.CreateDirectory(directory);
            var existingState = ReadState(profile);
            var devToolsFile = Path.Combine(directory, "DevToolsActivePort");
            var existingPort = TryReadDevToolsPort(directory);
            if (existingState is not null && existingPort is not null)
            {
                try
                {
                    var reconnected = await ConnectAsync(profile, directory, existingState, existingPort.Value, cancellationToken);
                    _sessions[profile] = reconnected;
                    return reconnected;
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    try { File.Delete(devToolsFile); } catch { }
                    DeleteState(profile);
                }
            }

            var reader = new ArgReader(args);
            var executable = ResolveExecutable(engine, reader.String("executable"));
            try { if (File.Exists(devToolsFile)) File.Delete(devToolsFile); } catch { }
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            startInfo.ArgumentList.Add("--user-data-dir=" + directory);
            startInfo.ArgumentList.Add("--remote-debugging-port=0");
            startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            if (reader.Boolean("headless")) startInfo.ArgumentList.Add("--headless=new");
            var initialUrl = reader.String("url", "about:blank") ?? "about:blank";
            startInfo.ArgumentList.Add(initialUrl);
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to launch browser '{executable}'.");
            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(reader.Int32("timeout_ms", 15_000), 1000, 120_000));
            var port = await WaitForDevToolsPortAsync(directory, process, timeout, cancellationToken);
            var state = new BrowserState(engine, executable, process.Id, DateTimeOffset.UtcNow);
            var session = await ConnectAsync(profile, directory, state, port, cancellationToken);
            _sessions[profile] = session;
            return session;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BrowserSession> ConnectAsync(
        string profile,
        string directory,
        BrowserState state,
        int port,
        CancellationToken cancellationToken)
    {
        var playwright = await GetPlaywrightAsync(cancellationToken);
        var browser = await playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{port}",
            new BrowserTypeConnectOverCDPOptions { Timeout = 15_000 });
        var resolvedPid = await ResolveBrowserPidAsync(browser, state.pid);
        var resolvedState = state with { pid = resolvedPid };
        WriteState(profile, resolvedState);
        return new BrowserSession(profile, directory, port, resolvedState, browser);
    }

    private static async Task<int> ResolveBrowserPidAsync(IBrowser browser, int fallbackPid)
    {
        try
        {
            var cdp = await browser.NewBrowserCDPSessionAsync();
            try
            {
                var raw = await cdp.SendAsync("SystemInfo.getProcessInfo");
                var json = JsonSerializer.SerializeToElement(raw);
                if (json.ValueKind != JsonValueKind.Object
                    || !json.TryGetProperty("processInfo", out var processes)
                    || processes.ValueKind != JsonValueKind.Array) return fallbackPid;
                foreach (var process in processes.EnumerateArray())
                {
                    if (!process.TryGetProperty("type", out var type)
                        || !string.Equals(type.GetString(), "browser", StringComparison.OrdinalIgnoreCase)
                        || !process.TryGetProperty("id", out var id)) continue;
                    if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && number > 0) return number;
                    if (id.ValueKind == JsonValueKind.String && int.TryParse(id.GetString(), out number) && number > 0) return number;
                }
            }
            finally
            {
                await cdp.DetachAsync();
            }
        }
        catch { }
        return fallbackPid;
    }

    private async Task<IPlaywright> GetPlaywrightAsync(CancellationToken cancellationToken)
    {
        if (_playwright is not null) return _playwright;
        await _playwrightGate.WaitAsync(cancellationToken);
        try
        {
            _playwright ??= await Playwright.CreateAsync();
            return _playwright;
        }
        finally
        {
            _playwrightGate.Release();
        }
    }

    private static IBrowserContext RequireContext(BrowserSession session) =>
        session.Browser.Contexts.FirstOrDefault()
        ?? throw new InvalidOperationException("The CDP browser has no browser context.");

    private async Task<IPage> ResolvePageAsync(BrowserSession session, IReadOnlyDictionary<string, JsonElement>? args, CancellationToken cancellationToken)
    {
        var context = RequireContext(session);
        if (context.Pages.Count == 0) return await context.NewPageAsync();
        var reader = new ArgReader(args);
        var index = reader.Int32("page_index", -1);
        if (index >= 0)
        {
            if (index >= context.Pages.Count) throw new ArgumentException($"page_index {index} is out of range.");
            return context.Pages[index];
        }
        var urlContains = reader.String("page_url_contains");
        var titleContains = reader.String("page_title_contains");
        if (!string.IsNullOrWhiteSpace(urlContains) || !string.IsNullOrWhiteSpace(titleContains))
        {
            foreach (var page in context.Pages.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(urlContains) && !page.Url.Contains(urlContains, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(titleContains))
                {
                    var title = await page.TitleAsync();
                    if (!title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)) continue;
                }
                return page;
            }
            throw new ArgumentException("No browser page matched the requested page selector.");
        }
        return context.Pages[^1];
    }

    private static async Task<IResponse?> NavigatePageAsync(IPage page, string url, ArgReader reader, CancellationToken cancellationToken)
    {
        var waitText = (reader.String("wait_until", "load") ?? "load").ToLowerInvariant();
        var wait = waitText switch
        {
            "commit" => WaitUntilState.Commit,
            "domcontentloaded" or "dom" => WaitUntilState.DOMContentLoaded,
            "load" => WaitUntilState.Load,
            "networkidle" or "network_idle" => WaitUntilState.NetworkIdle,
            _ => throw new ArgumentException("'wait_until' must be commit, domcontentloaded, load, or networkidle."),
        };
        return await page.GotoAsync(url, new PageGotoOptions { WaitUntil = wait, Timeout = Timeout(reader) });
    }

    private static async Task<object> DescribePageAsync(BrowserSession session, IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = RequireContext(session);
        var index = context.Pages.ToList().IndexOf(page);
        string title;
        try { title = await page.TitleAsync(); } catch { title = string.Empty; }
        return new { profile = session.Profile, index, url = page.Url, title, closed = page.IsClosed };
    }

    private static async Task<object> DescribeSessionAsync(BrowserSession session, CancellationToken cancellationToken)
    {
        var context = RequireContext(session);
        var tabs = new List<object>();
        for (var i = 0; i < context.Pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = context.Pages[i];
            string title;
            try { title = await page.TitleAsync(); } catch { title = string.Empty; }
            tabs.Add(new { index = i, url = page.Url, title });
        }
        return new
        {
            profile = session.Profile,
            engine = session.State.engine,
            executable = session.State.executable,
            pid = session.State.pid,
            devtools_port = session.Port,
            user_data_dir = session.UserDataDir,
            connected = session.Browser.IsConnected,
            tabs,
        };
    }

    private static async Task<int> WaitForDevToolsPortAsync(string directory, Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var file = Path.Combine(directory, "DevToolsActivePort");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var port = TryReadDevToolsPort(directory);
            if (port is > 0) return port.Value;
            await Task.Delay(100, cancellationToken);
        }
        var launcherDetail = process.HasExited ? $" Launcher exited with code {process.ExitCode}." : string.Empty;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        throw new TimeoutException("Browser did not expose its DevTools endpoint before timeout." + launcherDetail);
    }

    private static int? TryReadDevToolsPort(string directory)
    {
        var file = Path.Combine(directory, "DevToolsActivePort");
        try
        {
            if (!File.Exists(file)) return null;
            var first = File.ReadLines(file).FirstOrDefault();
            return int.TryParse(first, out var port) && port > 0 ? port : null;
        }
        catch { return null; }
    }

    private static string ResolveExecutable(string engine, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(explicitPath));
            if (!File.Exists(full)) throw new ArgumentException($"Browser executable does not exist: {full}");
            return full;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidates = engine switch
        {
            "chrome" => new[]
            {
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
            },
            "brave" => new[]
            {
                Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            },
            "edge" => new[]
            {
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
            },
            _ => throw new ArgumentException("'engine' must be chrome, brave, or edge."),
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"No installed {engine} executable was found. Provide 'executable' explicitly.");
    }

    private static string NormalizeProfile(string profile)
    {
        profile = profile.Trim();
        if (profile.Length is < 1 or > 64) throw new ArgumentException("Browser profile name must be 1-64 characters.");
        if (profile is "." or ".." || profile.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')))
            throw new ArgumentException("Browser profile name may contain only letters, digits, dot, dash, and underscore.");
        return profile;
    }

    private static string NormalizeEngine(string engine)
    {
        engine = engine.Trim().ToLowerInvariant();
        return engine switch
        {
            "chrome" or "google-chrome" => "chrome",
            "brave" => "brave",
            "edge" or "msedge" => "edge",
            _ => throw new ArgumentException("'engine' must be chrome, brave, or edge."),
        };
    }

    private static float Timeout(ArgReader reader) => Math.Clamp(reader.Int32("timeout_ms", 30_000), 100, 300_000);

    private static string ProfilesRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StealthEye",
        "browser",
        "profiles");

    private static string ProfileDirectory(string profile) => Path.Combine(ProfilesRoot(), profile);
    private static string StatePath(string profile) => Path.Combine(ProfileDirectory(profile), ".stealtheye-browser.json");
    private static string DefaultDownloadsRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "StealthEye");
    private static string DefaultScreenshotsRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StealthEye", "browser", "screenshots");

    private static BrowserState? ReadState(string profile)
    {
        try
        {
            var path = StatePath(profile);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BrowserState>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static void WriteState(string profile, BrowserState state)
    {
        var path = StatePath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void DeleteState(string profile)
    {
        try { File.Delete(StatePath(profile)); } catch { }
    }

    private static bool IsProcessRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N") + extension);
    }

    private sealed record BrowserSession(string Profile, string UserDataDir, int Port, BrowserState State, IBrowser Browser);
    private sealed record BrowserState(string engine, string executable, int pid, DateTimeOffset started_at);
}
