using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace ShablonRabot.App.Services;

/// <summary>Светлая/тёмная тема: подмена словаря токенов + цвет системного заголовка окна (DWM).</summary>
public static class ThemeService
{
    const int DwmwaUseImmersiveDarkMode = 20, DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static ResourceDictionary? _current;
    static string _mode = "system";

    public static bool IsDark { get; private set; }

    public static void Apply(string mode)
    {
        _mode = mode;
        IsDark = mode == "dark" || (mode == "system" && SystemIsDark());
        var dict = new ResourceDictionary { Source = new Uri($"Themes/Theme.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        if (SystemParameters.HighContrast) ApplyHighContrast(dict);
        merged.Insert(0, dict);
        _current = dict;
        foreach (Window w in Application.Current.Windows) ApplyCaption(w);
    }

    /// <summary>Высокая контрастность Windows: все кисти — системные цвета, без декоративных заливок (DESIGN.md 10).</summary>
    static void ApplyHighContrast(ResourceDictionary d)
    {
        Brush window = SystemColors.WindowBrush, text = SystemColors.WindowTextBrush, gray = SystemColors.GrayTextBrush;
        Brush highlight = SystemColors.HighlightBrush, onHighlight = SystemColors.HighlightTextBrush, link = SystemColors.HotTrackBrush;
        foreach (var key in d.Keys.OfType<string>().Where(k => k.StartsWith("Brush.")).ToList())
        {
            d[key] = key switch
            {
                _ when key.StartsWith("Brush.Paper.") => d[key], // лист всегда «бумажный»
                "Brush.Fill.Placeholder" or "Brush.Fill.MarginZone" or "Brush.Fill.MarginZoneActive" => d[key],
                _ when key.StartsWith("Brush.Accent.") => key == "Brush.Accent.Text" ? link : highlight,
                "Brush.Text.OnAccent" or "Brush.Thumb.OnAccent" or "Brush.Text.OnError" => onHighlight,
                "Brush.Text.Disabled" or "Brush.Text.OnAccentDisabled" => gray,
                _ when key.StartsWith("Brush.Text.") || key.StartsWith("Brush.Stroke.") || key.StartsWith("Brush.Focus.Outer") => text,
                "Brush.Fill.Error" => highlight,
                "Brush.Focus.Inner" => window,
                "Brush.Bg.Smoke" => Brushes.Transparent,
                _ => window,
            };
        }
    }

    public static void Watch()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Application.Current.Dispatcher.Invoke(() => Apply(_mode));
        };
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _mode == "system")
                Application.Current.Dispatcher.Invoke(() => Apply("system"));
        };
    }

    public static void ApplyCaption(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        if (Application.Current.TryFindResource("Color.Bg.Window") is Color c)
        {
            var colorref = c.R | (c.G << 8) | (c.B << 16);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorref, sizeof(int));
        }
    }

    static bool SystemIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }
}
