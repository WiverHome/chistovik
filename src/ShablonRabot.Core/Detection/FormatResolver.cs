using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ShablonRabot.Core.Detection;

/// <summary>Свойство абзаца/прогона: прямое форматирование → стиль и его родители → настройки по умолчанию.</summary>
internal sealed class FormatResolver
{
    readonly Dictionary<string, Style> _styles;
    readonly string? _defaultParaStyle;
    readonly OpenXmlElement? _pDefault, _rDefault;
    readonly string? _minorFont, _majorFont;

    internal const double TwipsPerMm = 56.6929, TwipsPerCm = 566.929;

    public FormatResolver(MainDocumentPart main)
    {
        var styles = main.StyleDefinitionsPart?.Styles;
        _styles = styles?.Elements<Style>().Where(s => s.StyleId?.Value != null)
            .GroupBy(s => s.StyleId!.Value!).ToDictionary(g => g.Key, g => g.First()) ?? new();
        _defaultParaStyle = styles?.Elements<Style>()
            .FirstOrDefault(s => s.Type?.Value == StyleValues.Paragraph && s.Default?.Value == true)?.StyleId?.Value;
        _pDefault = styles?.DocDefaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle;
        _rDefault = styles?.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
        var fontScheme = main.ThemePart?.Theme?.ThemeElements?.FontScheme;
        _minorFont = fontScheme?.MinorFont?.LatinFont?.Typeface?.Value;
        _majorFont = fontScheme?.MajorFont?.LatinFont?.Typeface?.Value;
    }

    IEnumerable<Style> Chain(string? styleId)
    {
        var seen = new HashSet<string>();
        while (styleId != null && seen.Add(styleId) && _styles.TryGetValue(styleId, out var s))
        {
            yield return s;
            styleId = s.BasedOn?.Val?.Value;
        }
    }

    IEnumerable<OpenXmlElement> ParagraphSources(Paragraph p)
    {
        if (p.ParagraphProperties != null) yield return p.ParagraphProperties;
        foreach (var s in Chain(p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? _defaultParaStyle))
            if (s.StyleParagraphProperties != null) yield return s.StyleParagraphProperties;
        if (_pDefault != null) yield return _pDefault;
    }

    IEnumerable<OpenXmlElement> RunSources(Paragraph p, Run? r)
    {
        if (r?.RunProperties != null) yield return r.RunProperties;
        foreach (var s in Chain(r?.RunProperties?.RunStyle?.Val?.Value))
            if (s.StyleRunProperties != null) yield return s.StyleRunProperties;
        foreach (var s in Chain(p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? _defaultParaStyle))
            if (s.StyleRunProperties != null) yield return s.StyleRunProperties;
        if (_rDefault != null) yield return _rDefault;
    }

    T? First<T>(IEnumerable<OpenXmlElement> sources) where T : OpenXmlElement =>
        sources.Select(s => s.GetFirstChild<T>()).FirstOrDefault(x => x != null);

    public string Justification(Paragraph p)
    {
        var jc = First<Justification>(ParagraphSources(p))?.Val;
        if (jc == null) return "left";
        var v = jc.Value;
        return v == JustificationValues.Both ? "both" : v == JustificationValues.Distribute ? "distribute"
            : v == JustificationValues.Center ? "center" : v == JustificationValues.Right || v == JustificationValues.End ? "right" : "left";
    }

    public double FirstLineIndentCm(Paragraph p)
    {
        foreach (var src in ParagraphSources(p))
        {
            if (src.GetFirstChild<Indentation>() is not { } ind) continue;
            if (ind.FirstLine?.Value is { } fl && double.TryParse(fl, out var f)) return f / TwipsPerCm;
            if (ind.Hanging?.Value is { } h && double.TryParse(h, out var hv)) return -hv / TwipsPerCm;
            if (ind.FirstLineChars?.Value is { } fc) return fc / 100.0 * 0.5; // приблизительно: символ ≈ 0,5 см
        }
        return 0;
    }

    /// <summary>Межстрочный интервал как множитель (1,0 / 1,15 / 1,5 / 2,0).</summary>
    public double LineSpacing(Paragraph p)
    {
        var sp = ParagraphSources(p).Select(s => s.GetFirstChild<SpacingBetweenLines>()).FirstOrDefault(s => s?.Line?.Value != null);
        if (sp?.Line?.Value is not { } lineText || !double.TryParse(lineText, out var line)) return 1.0;
        var rule = sp.LineRule?.Value;
        double multiple;
        if (rule == null || rule == LineSpacingRuleValues.Auto) multiple = line / 240.0;
        else
        {
            // Точный интервал в пунктах: делим на высоту одинарной строки (≈ 1,15 кегля)
            var size = FontSize(p, p.Elements<Run>().FirstOrDefault()) ?? 12;
            multiple = line / 20.0 / (size * 1.15);
        }
        double[] known = [1.0, 1.15, 1.5, 2.0];
        var nearest = known.OrderBy(k => Math.Abs(k - multiple)).First();
        return Math.Abs(nearest - multiple) < 0.08 ? nearest : Math.Round(multiple, 2);
    }

    public int? OutlineLevel(Paragraph p)
    {
        var lvl = First<OutlineLevel>(ParagraphSources(p))?.Val?.Value;
        if (lvl is >= 0 and < 9) return lvl;
        // Встроенные стили заголовков без явного уровня
        var name = Chain(p.ParagraphProperties?.ParagraphStyleId?.Val?.Value).FirstOrDefault()?.StyleName?.Val?.Value?.ToLowerInvariant() ?? "";
        var m = Regex.Match(name, @"^(heading|заголовок)\s*(\d)$");
        return m.Success ? int.Parse(m.Groups[2].Value) - 1 : null;
    }

    public bool IsTocStyle(Paragraph p)
    {
        var id = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        var name = Chain(id).FirstOrDefault()?.StyleName?.Val?.Value ?? "";
        return id.StartsWith("TOC", StringComparison.OrdinalIgnoreCase) || name.StartsWith("toc", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("оглавление", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsList(Paragraph p) => First<NumberingProperties>(ParagraphSources(p))?.NumberingId?.Val?.Value is > 0;

    public bool PageBreakBefore(Paragraph p) => First<PageBreakBefore>(ParagraphSources(p)) is { } b && (b.Val?.Value ?? true);

    public bool IsBold(Paragraph p)
    {
        var runs = p.Elements<Run>().Where(r => r.InnerText.Trim().Length > 0).ToList();
        return runs.Count > 0 && runs.All(r => First<Bold>(RunSources(p, r)) is { } b && (b.Val?.Value ?? true));
    }

    public bool IsCaps(Paragraph p)
    {
        var runs = p.Elements<Run>().Where(r => r.InnerText.Trim().Length > 0).ToList();
        return runs.Count > 0 && runs.All(r => First<Caps>(RunSources(p, r)) is { } c && (c.Val?.Value ?? true));
    }

    public string? Font(Paragraph p, Run r)
    {
        var f = First<RunFonts>(RunSources(p, r).Where(s => s.GetFirstChild<RunFonts>() is { } rf
            && (rf.Ascii?.Value != null || rf.HighAnsi?.Value != null || rf.AsciiTheme?.Value != null || rf.HighAnsiTheme?.Value != null)));
        if (f == null) return null;
        var name = f.Ascii?.Value ?? f.HighAnsi?.Value;
        if (name == null && (f.AsciiTheme ?? f.HighAnsiTheme) is { } theme)
            name = (theme.InnerText ?? "").StartsWith("major", StringComparison.OrdinalIgnoreCase) ? _majorFont : _minorFont;
        return name;
    }

    public double? FontSize(Paragraph p, Run? r)
    {
        var sz = First<FontSize>(RunSources(p, r))?.Val?.Value;
        return sz != null && double.TryParse(sz, out var half) ? half / 2 : null;
    }

    /// <summary>Цвет текста прогона: «auto», hex или null (не задан — чёрный по умолчанию).</summary>
    public string? Color(Paragraph p, Run r) => First<Color>(RunSources(p, r))?.Val?.Value;

    public bool IsBoldRun(Paragraph p, Run r) => First<Bold>(RunSources(p, r)) is { } b && (b.Val?.Value ?? true);
}
