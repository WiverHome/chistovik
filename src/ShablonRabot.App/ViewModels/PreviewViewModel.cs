using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ShablonRabot.App.Infrastructure;
using ShablonRabot.Core;

namespace ShablonRabot.App.ViewModels;

public enum PreviewMode { Title, Page, Toc }

/// <summary>Фрагмент строки на листе. FieldKey — поле титула, из которого он взят (для клика и подсветки).</summary>
public sealed record PreviewRun(string Text, bool IsPlaceholder, string? FieldKey);

public abstract class PreviewFocusable : ObservableObject
{
    bool _isFocused;
    public bool IsFocused { get => _isFocused; set => Set(ref _isFocused, value); }
}

/// <summary>Строка титульного листа. Размеры — в DIP листа 793,7×1122,5 (A4 при 96 DPI); масштабирует Viewbox.</summary>
public sealed class PreviewLine(IReadOnlyList<PreviewRun> runs, TextAlignment align, bool bold, double size, double spaceBefore, double indentLeft, bool italic = false)
    : PreviewFocusable
{
    public IReadOnlyList<PreviewRun> Runs { get; } = runs;
    public TextAlignment Align { get; } = align;
    public FontWeight Weight { get; } = bold ? FontWeights.Bold : FontWeights.Normal;
    public FontStyle Style { get; } = italic ? FontStyles.Italic : FontStyles.Normal;
    public double Size { get; } = size;
    public Thickness Margin { get; } = new(indentLeft, spaceBefore, 0, 0);
    public IEnumerable<string> Keys => Runs.Where(r => r.FieldKey != null).Select(r => r.FieldKey!);
}

public sealed class PreviewSignatureRow(IReadOnlyList<PreviewRun> left, IReadOnlyList<PreviewRun> right) : PreviewFocusable
{
    public IReadOnlyList<PreviewRun> Left { get; } = left;
    public IReadOnlyList<PreviewRun> Right { get; } = right;
    public IEnumerable<string> Keys => Left.Concat(Right).Where(r => r.FieldKey != null).Select(r => r.FieldKey!);
}

public sealed class PreviewSignatures(double spaceBefore, double left, double right, IReadOnlyList<PreviewSignatureRow> rows)
{
    public Thickness Margin { get; } = new(0, spaceBefore, 0, 0);
    public GridLength LeftColumn { get; } = new(left);
    public GridLength RightColumn { get; } = new(right);
    public IReadOnlyList<PreviewSignatureRow> Rows { get; } = rows;
}

/// <summary>Строка содержания: уровень, текст (возможно подсказка), условный номер страницы.</summary>
public sealed class PreviewTocLine(string number, string text, bool isPlaceholder, int level, int page, object? source) : PreviewFocusable
{
    public string Number { get; } = number;
    public string Text { get; } = text;
    public bool IsPlaceholder { get; } = isPlaceholder;
    public Thickness Indent { get; } = new(level * 18.9, 0, 0, 0);
    public int Page { get; } = page;
    public object? Source { get; } = source;
}

/// <summary>Живой предпросмотр (DESIGN.md 5).</summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    public const double PageWidth = 793.7, PageHeight = 1122.5;
    const double DipPerMm = 3.7795, DipPerPt = 1.3333, DipPerCm = 37.795;

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex FieldRegex();

    PreviewMode _mode = PreviewMode.Title;
    IReadOnlyList<object> _titleItems = [];
    IReadOnlyList<PreviewLine> _bottomLines = [];
    IReadOnlyList<PreviewTocLine> _toc = [];
    string? _focusedKey, _highlight;
    object? _focusedOutline;
    int _placeholderCount;
    Profile _profile = new();

    public PreviewMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(Legend));
            OnPropertyChanged(nameof(ShowLegendSwatch));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public IReadOnlyList<object> TitleItems { get => _titleItems; private set => Set(ref _titleItems, value); }
    public IReadOnlyList<PreviewLine> BottomLines { get => _bottomLines; private set => Set(ref _bottomLines, value); }
    public IReadOnlyList<PreviewTocLine> TocLines { get => _toc; private set => Set(ref _toc, value); }
    public int PlaceholderCount { get => _placeholderCount; private set { if (Set(ref _placeholderCount, value)) { OnPropertyChanged(nameof(Legend)); OnPropertyChanged(nameof(ShowLegendSwatch)); } } }

    public string Legend => Mode switch
    {
        PreviewMode.Title => PlaceholderCount > 0 ? "Подсвечено — не заполнено, останется подсказкой" : "Все поля заполнены",
        PreviewMode.Page => "Схема в масштабе. Переносы слов здесь не показаны",
        _ => "Номера страниц условные",
    };
    public bool ShowLegendSwatch => Mode == PreviewMode.Title && PlaceholderCount > 0;
    public string AutomationName => Mode switch
    {
        PreviewMode.Title => "Предпросмотр: титульный лист",
        PreviewMode.Page => "Предпросмотр: схема страницы",
        _ => "Предпросмотр: содержание",
    };

    // ------------------------------------------------------------ общие параметры листа

    public double Width => PageWidth;
    public double Height => PageHeight;
    public FontFamily FontFamily { get; private set; } = new("Times New Roman");
    public double TitleFontSize { get; private set; }
    public double TitleLineHeight => TitleFontSize * 1.15;

    // ------------------------------------------------------------ схема страницы

    public Thickness Margins { get; private set; }
    public double MarginLeft => Margins.Left;
    public double MarginRight => Margins.Right;
    public double MarginTop => Margins.Top;
    public double MarginBottom => Margins.Bottom;
    public string MarginLeftLabel { get; private set; } = "";
    public string MarginRightLabel { get; private set; } = "";
    public string MarginTopLabel { get; private set; } = "";
    public string MarginBottomLabel { get; private set; } = "";
    public double TextSize { get; private set; }
    public double TextLineHeight { get; private set; }
    public double IndentDip { get; private set; }
    public string IndentLabel { get; private set; } = "";
    public string LineSpacingLabel { get; private set; } = "";
    public TextAlignment TextAlign { get; private set; }
    public TextAlignment HeadingAlign { get; private set; }
    public Thickness HeadingMargin { get; private set; }
    public FontWeight HeadingWeight { get; private set; }
    public string HeadingText { get; private set; } = "";
    public string FigureCaption { get; private set; } = "";
    public double CaptionSize { get; private set; }
    public string TableCaption { get; private set; } = "";
    public TextAlignment TableCaptionAlign { get; private set; }
    public double TableTextSize { get; private set; }
    public HorizontalAlignment PageNumberAlign { get; private set; }
    public VerticalAlignment PageNumberVertical { get; private set; }
    public double PageNumberZone { get; private set; }
    public string TocTitle { get; private set; } = "СОДЕРЖАНИЕ";

    /// <summary>Правило в фокусе на странице «Правила» — подсвечивается на схеме.</summary>
    public string? Highlight { get => _highlight; set => Set(ref _highlight, value); }

    public string? FocusedKey
    {
        get => _focusedKey;
        set
        {
            if (!Set(ref _focusedKey, value)) return;
            ApplyFocus();
        }
    }

    public object? FocusedOutline
    {
        get => _focusedOutline;
        set
        {
            if (!Set(ref _focusedOutline, value)) return;
            foreach (var l in TocLines) l.IsFocused = value != null && ReferenceEquals(l.Source, value);
        }
    }

    public void Update(Profile p, StructureViewModel structure)
    {
        _profile = p;
        var fields = Placeholders.Resolve(p);
        var placeholders = 0;

        IReadOnlyList<PreviewRun> Runs(string template)
        {
            var runs = new List<PreviewRun>();
            var last = 0;
            foreach (Match m in FieldRegex().Matches(template))
            {
                if (m.Index > last) runs.Add(new PreviewRun(template[last..m.Index], false, null));
                var key = m.Groups[1].Value;
                var value = fields.GetValueOrDefault(key, m.Value);
                var isHint = Placeholders.IsPlaceholder(value);
                if (isHint) placeholders++;
                runs.Add(new PreviewRun(value, isHint, key));
                last = m.Index + m.Length;
            }
            if (last < template.Length) runs.Add(new PreviewRun(template[last..], false, null));
            return runs;
        }

        var tp = p.TitlePage;
        FontFamily = new FontFamily(p.Text.Font + ", Times New Roman");
        TitleFontSize = tp.FontSizePt * DipPerPt;
        var items = new List<object>();
        foreach (var block in tp.Blocks)
        {
            if (block.Type == "signatures")
            {
                var w = block.ColWidthsCm;
                items.Add(new PreviewSignatures(block.SpaceBeforePt * DipPerPt + TitleLineHeight,
                    (w.ElementAtOrDefault(0) is > 0 and var a ? a : 6) * DipPerCm,
                    (w.ElementAtOrDefault(1) is > 0 and var b ? b : 9) * DipPerCm,
                    block.Rows.Select(r => new PreviewSignatureRow(Runs(r.ElementAtOrDefault(0) ?? ""), Runs(r.ElementAtOrDefault(1) ?? ""))).ToList()));
                continue;
            }
            for (var i = 0; i < block.Lines.Count; i++)
                items.Add(new PreviewLine(Runs(block.Lines[i]), Align(block.Align), block.Bold, (block.SizePt ?? tp.FontSizePt) * DipPerPt,
                    i == 0 ? block.SpaceBeforePt * DipPerPt : 0, block.IndentLeftCm * DipPerCm, block.Italic));
        }
        TitleItems = items;
        BottomLines = tp.BottomLines.Select(l => new PreviewLine(Runs(l), TextAlignment.Center, false, TitleFontSize, 0, 0)).ToList();
        PlaceholderCount = placeholders;

        // Схема страницы
        var mm = p.Page.MarginsMm;
        Margins = new Thickness(mm.Left * DipPerMm, mm.Top * DipPerMm, mm.Right * DipPerMm, mm.Bottom * DipPerMm);
        MarginLeftLabel = $"{NumberRule.Format(mm.Left)} мм";
        MarginRightLabel = $"{NumberRule.Format(mm.Right)} мм";
        MarginTopLabel = $"{NumberRule.Format(mm.Top)} мм";
        MarginBottomLabel = $"{NumberRule.Format(mm.Bottom)} мм";
        TextSize = p.Text.SizePt * DipPerPt;
        TextLineHeight = TextSize * 1.15 * p.Text.LineSpacing;
        IndentDip = p.Text.FirstLineIndentCm * DipPerCm;
        IndentLabel = $"{NumberRule.Format(p.Text.FirstLineIndentCm)} см";
        LineSpacingLabel = NumberRule.Format(p.Text.LineSpacing);
        TextAlign = p.Text.Align == "justify" ? TextAlignment.Justify : TextAlignment.Left;
        var h1 = p.Headings.Level1;
        HeadingAlign = h1.Align == "center" ? TextAlignment.Center : TextAlignment.Left;
        HeadingMargin = new Thickness(h1.Align == "center" ? 0 : IndentDip, 0, 0, h1.SpaceAfterPt * DipPerPt);
        HeadingWeight = h1.Bold ? FontWeights.Bold : FontWeights.Normal;
        HeadingText = "1 Название главы";
        var cap = p.Captions;
        FigureCaption = $"{cap.Figure.Prefix} 1{cap.Separator}Схема процесса";
        CaptionSize = cap.Figure.SizePt * DipPerPt;
        TableCaption = $"{cap.Table.Prefix} 1{cap.Separator}Исходные данные";
        TableCaptionAlign = cap.Table.Align == "center" ? TextAlignment.Center : TextAlignment.Left;
        TableTextSize = cap.TableTextSizePt * DipPerPt;
        var top = p.Page.Numbering.Align.StartsWith("top-", StringComparison.Ordinal);
        PageNumberAlign = p.Page.Numbering.Align.EndsWith("right", StringComparison.Ordinal) ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        PageNumberVertical = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        PageNumberZone = top ? Margins.Top : Margins.Bottom;

        // Содержание
        var upper = p.Headings.Structural.Case == "upper";
        string S(string t) => upper ? t.ToUpperInvariant() : t;
        TocTitle = S("Содержание");
        var toc = new List<PreviewTocLine>();
        var page = 3;
        if (structure.Introduction) toc.Add(new PreviewTocLine("", S("Введение"), false, 0, page, null));
        foreach (var c in structure.Chapters)
        {
            page += 1;
            toc.Add(new PreviewTocLine(c.Number, Title(c.Title, c.ProfileHint?.Trim('[', ']') ?? $"Название главы {c.Number}", out var ph), ph, 0, page, c));
            if (p.Headings.TocLevels >= 2)
                foreach (var s in c.Sections)
                {
                    page += 2;
                    toc.Add(new PreviewTocLine(s.Number, Title(s.Title, s.ProfileHint?.Trim('[', ']') ?? $"Название параграфа {s.Number}", out var sph), sph, 1, page, s));
                }
        }
        page += 2;
        if (structure.Conclusion) toc.Add(new PreviewTocLine("", S("Заключение"), false, 0, page++, null));
        if (structure.References) toc.Add(new PreviewTocLine("", S("Список использованных источников"), false, 0, page++, null));
        foreach (var letter in p.Structure.Where(i => i.Type == "appendix").Select(i => i.Letter))
            toc.Add(new PreviewTocLine("", S($"Приложение {letter}"), false, 0, page++, null));
        TocLines = toc;

        OnPropertyChanged(string.Empty); // все производные свойства листа
        ApplyFocus();
        FocusedOutline = _focusedOutline;
        foreach (var l in TocLines) l.IsFocused = _focusedOutline != null && ReferenceEquals(l.Source, _focusedOutline);
    }

    static string Title(string title, string fallback, out bool placeholder)
    {
        placeholder = string.IsNullOrWhiteSpace(title);
        return placeholder ? $"[{fallback}]" : title.Trim();
    }

    void ApplyFocus()
    {
        foreach (var item in TitleItems)
        {
            if (item is PreviewLine line) line.IsFocused = _focusedKey != null && line.Keys.Contains(_focusedKey);
            if (item is PreviewSignatures sig)
                foreach (var row in sig.Rows) row.IsFocused = _focusedKey != null && row.Keys.Contains(_focusedKey);
        }
        foreach (var line in BottomLines) line.IsFocused = _focusedKey != null && line.Keys.Contains(_focusedKey);
    }

    static TextAlignment Align(string a) => a switch
    {
        "left" => TextAlignment.Left,
        "right" => TextAlignment.Right,
        "justify" => TextAlignment.Justify,
        _ => TextAlignment.Center,
    };
}
