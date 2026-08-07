using System.Globalization;
using System.Text.Json;
using UIA = Interop.UIAutomationClient;
using StealthEye.Runtime;

namespace StealthEye.Windows;

public sealed class UiAutomationOperations
{
    private const int InvokePatternId = 10000;
    private const int ValuePatternId = 10002;
    private const int ExpandCollapsePatternId = 10005;
    private const int SelectionItemPatternId = 10010;
    private const int TogglePatternId = 10015;
    private const int ScrollItemPatternId = 10017;

    private static readonly IReadOnlyDictionary<string, int> ControlTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["button"] = 50000,
        ["calendar"] = 50001,
        ["checkbox"] = 50002,
        ["combo"] = 50003,
        ["combobox"] = 50003,
        ["edit"] = 50004,
        ["hyperlink"] = 50005,
        ["image"] = 50006,
        ["listitem"] = 50007,
        ["list"] = 50008,
        ["menu"] = 50009,
        ["menubar"] = 50010,
        ["menuitem"] = 50011,
        ["progressbar"] = 50012,
        ["radiobutton"] = 50013,
        ["scrollbar"] = 50014,
        ["slider"] = 50015,
        ["spinner"] = 50016,
        ["statusbar"] = 50017,
        ["tab"] = 50018,
        ["tabitem"] = 50019,
        ["text"] = 50020,
        ["toolbar"] = 50021,
        ["tooltip"] = 50022,
        ["tree"] = 50023,
        ["treeitem"] = 50024,
        ["custom"] = 50025,
        ["group"] = 50026,
        ["thumb"] = 50027,
        ["datagrid"] = 50028,
        ["dataitem"] = 50029,
        ["document"] = 50030,
        ["splitbutton"] = 50031,
        ["window"] = 50032,
        ["pane"] = 50033,
        ["header"] = 50034,
        ["headeritem"] = 50035,
        ["table"] = 50036,
        ["titlebar"] = 50037,
        ["separator"] = 50038,
        ["semanticzoom"] = 50039,
        ["appbar"] = 50040,
    };

    public object Find(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var automation = NewAutomation();
        var reader = new ArgReader(args);
        var maxEntries = Math.Clamp(reader.Int32("max_entries", 200), 1, 5000);
        var matches = FindElements(automation, args, maxEntries);
        return new
        {
            elements = matches.Select(Describe).ToArray(),
            count = matches.Count,
            truncated = matches.Count >= maxEntries,
        };
    }

    public object Focused()
    {
        var automation = NewAutomation();
        return Describe(automation.GetFocusedElement());
    }

    public object FromPoint(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        if (!reader.Has("x") || !reader.Has("y")) throw new ArgumentException("ui.from_point requires 'x' and 'y'.");
        var automation = NewAutomation();
        var point = new UIA.tagPOINT { x = reader.Int32("x"), y = reader.Int32("y") };
        return Describe(automation.ElementFromPoint(point));
    }

    public object Focus(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var automation = NewAutomation();
        var element = ResolveOne(automation, args);
        element.SetFocus();
        return Describe(element);
    }

    public object Invoke(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var automation = NewAutomation();
        var element = ResolveOne(automation, args);
        var pattern = GetPattern<UIA.IUIAutomationInvokePattern>(element, InvokePatternId, "Invoke");
        pattern.Invoke();
        return Describe(element);
    }

    public object Value(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var automation = NewAutomation();
        var element = ResolveOne(automation, args);
        var pattern = GetPattern<UIA.IUIAutomationValuePattern>(element, ValuePatternId, "Value");
        var reader = new ArgReader(args);
        if (reader.Has("value")) pattern.SetValue(reader.String("value") ?? string.Empty);
        return new
        {
            element = Describe(element),
            value = pattern.CurrentValue,
            read_only = pattern.CurrentIsReadOnly != 0,
        };
    }

    public object Toggle(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var automation = NewAutomation();
        var element = ResolveOne(automation, args);
        var pattern = GetPattern<UIA.IUIAutomationTogglePattern>(element, TogglePatternId, "Toggle");
        pattern.Toggle();
        return new { element = Describe(element), state = pattern.CurrentToggleState.ToString() };
    }

    public object Select(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var automation = NewAutomation();
        var element = ResolveOne(automation, args);
        var pattern = GetPattern<UIA.IUIAutomationSelectionItemPattern>(element, SelectionItemPatternId, "SelectionItem");
        var action = (new ArgReader(args).String("action", "select") ?? "select").ToLowerInvariant();
        switch (action)
        {
            case "select": pattern.Select(); break;
            case "add": pattern.AddToSelection(); break;
            case "remove": pattern.RemoveFromSelection(); break;
            default: throw new ArgumentException("'action' must be select, add, or remove.");
        }
        return new { element = Describe(element), selected = pattern.CurrentIsSelected != 0, action };
    }

    public object Expand(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var automation = NewAutomation();
        var element = ResolveOne(automation, args);
        var pattern = GetPattern<UIA.IUIAutomationExpandCollapsePattern>(element, ExpandCollapsePatternId, "ExpandCollapse");
        var action = (new ArgReader(args).String("action", "expand") ?? "expand").ToLowerInvariant();
        switch (action)
        {
            case "expand": pattern.Expand(); break;
            case "collapse": pattern.Collapse(); break;
            default: throw new ArgumentException("'action' must be expand or collapse.");
        }
        return new { element = Describe(element), state = pattern.CurrentExpandCollapseState.ToString(), action };
    }

    public object ScrollIntoView(IReadOnlyDictionary<string, JsonElement>? args)
    {
        var automation = NewAutomation();
        var element = ResolveOne(automation, args);
        var pattern = GetPattern<UIA.IUIAutomationScrollItemPattern>(element, ScrollItemPatternId, "ScrollItem");
        pattern.ScrollIntoView();
        return Describe(element);
    }

    private static UIA.CUIAutomation8 NewAutomation() => new();

    private static List<UIA.IUIAutomationElement> FindElements(
        UIA.IUIAutomation automation,
        IReadOnlyDictionary<string, JsonElement>? args,
        int maxEntries)
    {
        var reader = new ArgReader(args);
        var root = ResolveRoot(automation, args);
        var defaultScope = HasElementSelector(args) ? "subtree" : "children";
        var scopeText = (reader.String("scope", defaultScope) ?? defaultScope).ToLowerInvariant();
        var scope = scopeText switch
        {
            "element" => UIA.TreeScope.TreeScope_Element,
            "children" => UIA.TreeScope.TreeScope_Children,
            "descendants" => UIA.TreeScope.TreeScope_Descendants,
            "subtree" => UIA.TreeScope.TreeScope_Subtree,
            _ => throw new ArgumentException("'scope' must be element, children, descendants, or subtree."),
        };
        var all = root.FindAll(scope, automation.CreateTrueCondition());
        var results = new List<UIA.IUIAutomationElement>();
        for (var i = 0; i < all.Length && results.Count < maxEntries; i++)
        {
            var element = all.GetElement(i);
            if (Matches(element, args)) results.Add(element);
        }
        return results;
    }

    private static UIA.IUIAutomationElement ResolveRoot(
        UIA.IUIAutomation automation,
        IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var handleText = reader.String("root_handle");
        if (!string.IsNullOrWhiteSpace(handleText))
            return automation.ElementFromHandle(ParseHandle(handleText));
        if (reader.Int32("root_process_id", 0) > 0 || !string.IsNullOrWhiteSpace(reader.String("root_title_contains")))
        {
            var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (reader.Int32("root_process_id", 0) > 0)
                map["process_id"] = JsonSerializer.SerializeToElement(reader.Int32("root_process_id"));
            if (!string.IsNullOrWhiteSpace(reader.String("root_title_contains")))
                map["title_contains"] = JsonSerializer.SerializeToElement(reader.String("root_title_contains"));
            return automation.ElementFromHandle(DesktopOperations.ResolveWindowHandle(map));
        }
        return automation.GetRootElement();
    }

    private static UIA.IUIAutomationElement ResolveOne(
        UIA.IUIAutomation automation,
        IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        if (reader.Boolean("focused")) return automation.GetFocusedElement();
        if (reader.Has("point_x") && reader.Has("point_y"))
            return automation.ElementFromPoint(new UIA.tagPOINT { x = reader.Int32("point_x"), y = reader.Int32("point_y") });

        if (!HasElementSelector(args))
        {
            var rootHandle = reader.String("root_handle");
            if (!string.IsNullOrWhiteSpace(rootHandle)) return automation.ElementFromHandle(ParseHandle(rootHandle));
            throw new ArgumentException("Provide an element selector, focused=true, point_x/point_y, or root_handle.");
        }

        var index = Math.Max(0, reader.Int32("index"));
        var matches = FindElements(automation, args, index + 1);
        if (matches.Count <= index) throw new ArgumentException("No matching UI Automation element was found.");
        return matches[index];
    }

    private static bool HasElementSelector(IReadOnlyDictionary<string, JsonElement>? args)
    {
        if (args is null) return false;
        return args.ContainsKey("name") || args.ContainsKey("automation_id") || args.ContainsKey("class_name")
            || args.ContainsKey("control_type") || args.ContainsKey("process_id") || args.ContainsKey("native_handle")
            || args.ContainsKey("runtime_id");
    }

    private static bool Matches(UIA.IUIAutomationElement element, IReadOnlyDictionary<string, JsonElement>? args)
    {
        var reader = new ArgReader(args);
        var exact = reader.Boolean("exact");
        var name = reader.String("name");
        if (!MatchesString(element.CurrentName, name, exact)) return false;
        var automationId = reader.String("automation_id");
        if (!MatchesString(element.CurrentAutomationId, automationId, exact)) return false;
        var className = reader.String("class_name");
        if (!MatchesString(element.CurrentClassName, className, exact)) return false;
        var processId = reader.Int32("process_id", 0);
        if (processId > 0 && element.CurrentProcessId != processId) return false;
        var nativeHandle = reader.String("native_handle");
        if (!string.IsNullOrWhiteSpace(nativeHandle) && new IntPtr(element.CurrentNativeWindowHandle) != ParseHandle(nativeHandle)) return false;
        if (reader.Has("control_type"))
        {
            var expected = ParseControlType(reader.String("control_type") ?? string.Empty);
            if (element.CurrentControlType != expected) return false;
        }
        if (args is not null && args.TryGetValue("runtime_id", out var runtimeIdElement) && runtimeIdElement.ValueKind == JsonValueKind.Array)
        {
            var expected = runtimeIdElement.EnumerateArray().Select(item => item.GetInt32()).ToArray();
            if (!element.GetRuntimeId().SequenceEqual(expected)) return false;
        }
        return true;
    }

    private static bool MatchesString(string? actual, string? expected, bool exact)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        actual ??= string.Empty;
        return exact
            ? string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            : actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static object Describe(UIA.IUIAutomationElement element)
    {
        var rect = element.CurrentBoundingRectangle;
        return new
        {
            name = element.CurrentName,
            automation_id = element.CurrentAutomationId,
            class_name = element.CurrentClassName,
            framework_id = element.CurrentFrameworkId,
            control_type = element.CurrentControlType,
            localized_control_type = element.CurrentLocalizedControlType,
            process_id = element.CurrentProcessId,
            native_handle = element.CurrentNativeWindowHandle == 0 ? null : "0x" + element.CurrentNativeWindowHandle.ToString("X", CultureInfo.InvariantCulture),
            enabled = element.CurrentIsEnabled != 0,
            offscreen = element.CurrentIsOffscreen != 0,
            keyboard_focus = element.CurrentHasKeyboardFocus != 0,
            focusable = element.CurrentIsKeyboardFocusable != 0,
            runtime_id = element.GetRuntimeId(),
            rect = new
            {
                left = rect.left,
                top = rect.top,
                right = rect.right,
                bottom = rect.bottom,
                width = rect.right - rect.left,
                height = rect.bottom - rect.top,
            },
        };
    }

    private static T GetPattern<T>(UIA.IUIAutomationElement element, int patternId, string name) where T : class
    {
        var pattern = element.GetCurrentPattern(patternId) as T;
        return pattern ?? throw new NotSupportedException($"UI element does not support the {name} pattern.");
    }

    private static int ParseControlType(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric)) return numeric;
        var normalized = value.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
        if (ControlTypes.TryGetValue(normalized, out var controlType)) return controlType;
        throw new ArgumentException($"Unknown UI Automation control type '{value}'. Use a numeric UIA control type ID or a standard name such as button, edit, document, pane, or window.");
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
        if (!long.TryParse(text, style, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
            throw new ArgumentException("Handle must be a nonzero decimal or 0x-prefixed hexadecimal value.");
        return new IntPtr(parsed);
    }
}
