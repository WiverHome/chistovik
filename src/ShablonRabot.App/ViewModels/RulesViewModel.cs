using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ShablonRabot.App.Infrastructure;
using ShablonRabot.Core;
using ShablonRabot.Core.Detection;

namespace ShablonRabot.App.ViewModels;

/// <summary>Правила оформления (DESIGN.md 4.3): 16 правил, проверка значений, отличия от эталона.</summary>
public sealed class RulesViewModel : ObservableObject, INotifyDataErrorInfo
{
    public static readonly Choice[] PageNumberAligns = [new("center", "Внизу по центру"), new("right", "Внизу справа"), new("top-center", "Вверху по центру")];
    public static readonly Choice[] LineSpacings = [new(1.0, "1,0"), new(1.15, "1,15"), new(1.5, "1,5"), new(2.0, "2,0")];
    public static readonly Choice[] TextAligns = [new("justify", "По ширине"), new("left", "По левому краю")];
    public static readonly Choice[] HeadingAligns = [new("indent", "С абзацного отступа"), new("center", "По центру")];
    public static readonly Choice[] TocLevelChoices = [new(1, "1"), new(2, "2"), new(3, "3")];
    public static readonly Choice[] FigurePrefixes = [new("Рисунок", "Рисунок"), new("Рис.", "Рис.")];
    public static readonly Choice[] Separators = [new(" – ", "Тире"), new(". ", "Точка")];
    public static readonly Choice[] TableCaptionAligns = [new("left", "Слева"), new("center", "По центру")];

    static readonly string[] PresetFonts = ["Times New Roman", "Arial", "Calibri", "PT Serif", "PT Astra Serif"];
    static readonly Lazy<HashSet<string>> InstalledFonts = new(() =>
        Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase));
    public static IReadOnlyList<string> FontList { get; } = PresetFonts
        .Concat(Fonts.SystemFontFamilies.Select(f => f.Source).Where(f => !f.StartsWith('@')).OrderBy(f => f))
        .Distinct().ToList();

    readonly Dictionary<string, RuleDiff> _diffs = new();
    /// <summary>Правила, взятые из примера (DESIGN.md 17.5, п. 4); значение — уверенность.</summary>
    readonly Dictionary<string, Confidence> _fromSample = new();
    bool _applyingSample;

    static readonly Dictionary<string, string> PropertyKeys = new()
    {
        [nameof(PageNumberAlign)] = "pageNumber", [nameof(Font)] = "font", [nameof(LineSpacing)] = "lineSpacing",
        [nameof(TextAlign)] = "align", [nameof(Hyphenation)] = "hyphenation", [nameof(HeadingAlign)] = "headingAlign",
        [nameof(ChapterNewPage)] = "chapterNewPage", [nameof(StructuralUpper)] = "structuralUpper", [nameof(TocLevels)] = "tocLevels",
        [nameof(FigurePrefix)] = "figurePrefix", [nameof(Separator)] = "separator", [nameof(TableCaptionAlign)] = "tableCaptionAlign",
    };
    Profile _baseline = new();
    string? _baselineGost = DefaultGost;
    string _baselineName = "";
    bool _loading;

    string _pageNumberAlign = "center", _font = "", _textAlign = "justify", _headingAlign = "indent";
    string _figurePrefix = "Рисунок", _separator = " – ", _tableCaptionAlign = "left";
    double _lineSpacing = 1.5;
    bool _hyphenation, _chapterNewPage, _structuralUpper;
    int _tocLevels = 3;
    string? _fontError;

    public RulesViewModel()
    {
        foreach (var (r, key) in new[] { (MarginLeft, "margins"), (MarginRight, "margins"), (MarginTop, "margins"), (MarginBottom, "margins"),
                                         (FontSize, "fontSize"), (Indent, "indent"), (TableFontSize, "tableFontSize") })
        {
            r.Changed += (_, _) => { ManualChange(key); OnChanged(typing: true); };
            r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NumberRule.Error)) RaiseErrors(); };
        }
        (string, string)[] names =
        [
            ("margins", "Поля страницы"), ("pageNumber", "Номер страницы"), ("font", "Шрифт"), ("fontSize", "Размер шрифта"),
            ("lineSpacing", "Межстрочный интервал"), ("indent", "Абзацный отступ"), ("align", "Выравнивание"),
            ("hyphenation", "Автоматические переносы"), ("headingAlign", "Положение заголовков глав"),
            ("chapterNewPage", "Каждая глава с новой страницы"), ("structuralUpper", "Структурные заголовки прописными"),
            ("tocLevels", "Уровни в содержании"), ("figurePrefix", "Подпись рисунка"), ("separator", "Знак в подписи"),
            ("tableCaptionAlign", "Подпись таблицы"), ("tableFontSize", "Шрифт в таблицах"),
        ];
        foreach (var (key, name) in names) _diffs[key] = new RuleDiff(key, name);
        RevertCommand = new RelayCommand(p => { if (p is string key) Revert(key); });
    }

    public event EventHandler? Changed;
    /// <summary>После «Вернуть» фокус переходит на контрол правила (ссылка исчезает).</summary>
    public event Action<string>? Reverted;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    // ------------------------------------------------------------ правила

    public NumberRule MarginLeft { get; } = new(0, 50, false, "Введите число от 0 до 50");
    public NumberRule MarginRight { get; } = new(0, 50, false, "Введите число от 0 до 50");
    public NumberRule MarginTop { get; } = new(0, 50, false, "Введите число от 0 до 50");
    public NumberRule MarginBottom { get; } = new(0, 50, false, "Введите число от 0 до 50");
    public NumberRule FontSize { get; } = new(10, 16, true, "Введите целое число от 10 до 16");
    public NumberRule Indent { get; } = new(0, 2.5, false, "Введите число от 0 до 2,5");
    public NumberRule TableFontSize { get; } = new(10, 14, true, "Введите целое число от 10 до 14");

    public string PageNumberAlign { get => _pageNumberAlign; set => SetRule(ref _pageNumberAlign, value); }
    public string Font
    {
        get => _font;
        set
        {
            if (!SetRule(ref _font, value)) return;
            _fontError = string.IsNullOrWhiteSpace(value) ? "Укажите шрифт" : null;
            OnPropertyChanged(nameof(FontWarning));
            RaiseErrors(nameof(Font));
        }
    }
    public double LineSpacing { get => _lineSpacing; set => SetRule(ref _lineSpacing, value); }
    public string TextAlign { get => _textAlign; set => SetRule(ref _textAlign, value); }
    public bool Hyphenation { get => _hyphenation; set => SetRule(ref _hyphenation, value); }
    public string HeadingAlign { get => _headingAlign; set => SetRule(ref _headingAlign, value); }
    public bool ChapterNewPage { get => _chapterNewPage; set => SetRule(ref _chapterNewPage, value); }
    public bool StructuralUpper { get => _structuralUpper; set => SetRule(ref _structuralUpper, value); }
    public int TocLevels { get => _tocLevels; set => SetRule(ref _tocLevels, value); }
    public string FigurePrefix { get => _figurePrefix; set => SetRule(ref _figurePrefix, value); }
    public string Separator { get => _separator; set => SetRule(ref _separator, value); }
    public string TableCaptionAlign { get => _tableCaptionAlign; set => SetRule(ref _tableCaptionAlign, value); }

    // ------------------------------------------------------------ подсказки и сводки

    public string? FontWarning => !string.IsNullOrWhiteSpace(Font) && !InstalledFonts.Value.Contains(Font.Trim())
        ? $"Шрифт «{Font.Trim()}» не установлен на этом компьютере — Word заменит его другим" : null;

    public string? MarginsError => new[] { MarginLeft, MarginRight, MarginTop, MarginBottom }.Select(m => m.Error).FirstOrDefault(e => e != null);

    public string? MarginsWarning => MarginsError == null && MarginLeft.Value + MarginRight.Value > 80
        ? $"Широкие поля: для текста остаётся {210 - MarginLeft.Value - MarginRight.Value:0} мм" : null;

    public string? TableFontWarning => TableFontSize.Error == null && TableFontSize.Value > FontSize.Value
        ? "Обычно текст в таблицах не крупнее основного" : null;

    public string SeparatorExample => $"Пример: {FigurePrefix} 1{Separator}Схема процесса";

    public RuleDiff this[string key] => _diffs[key];
    public IReadOnlyDictionary<string, RuleDiff> Diffs => _diffs;
    public int ChangedCount => _diffs.Values.Count(d => d.IsChanged);
    public int ErrorCount => new[] { MarginLeft, MarginRight, MarginTop, MarginBottom, FontSize, Indent, TableFontSize }.Count(r => r.HasErrors)
                             + (_fontError != null ? 1 : 0);
    public bool HasErrors => ErrorCount > 0;
    /// <summary>Стандарт по умолчанию: к нему сбрасываются правила импортированных профилей.</summary>
    public const string DefaultGost = "ГОСТ 7.32-2017";
    public bool BaselineIsGost => _baselineGost != null;
    public string DefaultsText => $"По умолчанию — {_baselineGost ?? DefaultGost}. Меняйте правила, только если методичка вашего вуза требует иначе.";

    public string StatusText => ChangedCount > 0
        ? $"Изменено: {ChangedCount} из 16"
        : _baselineGost != null ? $"Соответствует {_baselineGost}" : $"Как в профиле «{_baselineName}»";

    public string NavSubtitle => ErrorCount > 0 ? $"Ошибок: {ErrorCount}"
        : ChangedCount > 0 ? $"Изменено: {ChangedCount}"
        : _baselineGost ?? "Как в профиле";

    public RelayCommand RevertCommand { get; }

    public IEnumerable<string> GetErrors(string? propertyName) =>
        propertyName == nameof(Font) && _fontError != null ? [_fontError] : [];
    System.Collections.IEnumerable INotifyDataErrorInfo.GetErrors(string? propertyName) => GetErrors(propertyName);

    // ------------------------------------------------------------ загрузка и применение

    /// <summary>Эталон для отметок «Изменено»: ГОСТ (встроенные профили) или исходный импортированный профиль (gost = null).</summary>
    public void SetBaseline(Profile baseline, string? gost, string name)
    {
        _baseline = baseline;
        _baselineGost = gost;
        _baselineName = name;
        UpdateDiffs();
        OnPropertyChanged(nameof(DefaultsText));
        OnPropertyChanged(nameof(FontSizeHelp));
        OnPropertyChanged(nameof(IndentHelp));
        OnPropertyChanged(nameof(HeadingHelp));
    }

    // Подсказки к правилам — по ГОСТу выбранного вида работы, а не всегда по ГОСТ 7.32 (design/GOSTS.md)
    public string FontSizeHelp => "Кегль основного текста. " + (_baselineGost switch
    {
        "ГОСТ Р 2.105-2019" => "По ГОСТ Р 2.105 — 11–14 пт.",
        "ГОСТ Р 7.0.11-2011" => "По ГОСТ Р 7.0.11 — 12–14 пт.",
        null => "Чаще всего требуют 14 пт.",
        _ => "По ГОСТ — не меньше 12 пт, чаще требуют 14.",
    }) + " Заголовки и подписи того же размера изменятся вместе с ним.";

    public string IndentHelp => "Отступ первой строки абзаца («красная строка»). " + _baselineGost switch
    {
        "ГОСТ Р 2.105-2019" => "По ГОСТ Р 2.105 — пять знаков, 12,5–17 мм.",
        "ГОСТ Р 7.0.11-2011" => "По ГОСТ Р 7.0.11 — пять знаков, обычно 1,25 см.",
        null => "Чаще всего — 1,25 см.",
        _ => "По ГОСТ — 1,25 см.",
    };

    public string HeadingHelp => "Заголовки глав, параграфов и пунктов. " + _baselineGost switch
    {
        "ГОСТ Р 2.105-2019" => "По ГОСТ Р 2.105 — с абзацного отступа.",
        "ГОСТ Р 7.0.11-2011" => "По ГОСТ Р 7.0.11 — посередине страницы.",
        null => "Методички требуют по центру или с абзацного отступа.",
        _ => "По ГОСТ — с абзацного отступа, но многие методички требуют по центру.",
    };

    public void Load(Profile p)
    {
        _loading = true;
        var m = p.Page.MarginsMm;
        MarginLeft.Load(m.Left);
        MarginRight.Load(m.Right);
        MarginTop.Load(m.Top);
        MarginBottom.Load(m.Bottom);
        PageNumberAlign = p.Page.Numbering.Align;
        Font = p.Text.Font;
        FontSize.Load(p.Text.SizePt);
        LineSpacing = p.Text.LineSpacing;
        Indent.Load(p.Text.FirstLineIndentCm);
        TextAlign = p.Text.Align;
        Hyphenation = p.Text.Hyphenation;
        HeadingAlign = p.Headings.Level1.Align;
        ChapterNewPage = p.Headings.Level1.NewPage;
        StructuralUpper = p.Headings.Structural.Case == "upper";
        TocLevels = p.Headings.TocLevels;
        FigurePrefix = p.Captions.Figure.Prefix;
        Separator = p.Captions.Separator;
        TableCaptionAlign = p.Captions.Table.Align;
        TableFontSize.Load(p.Captions.TableTextSizePt);
        _loading = false;
        RaiseErrors();
        UpdateDiffs();
    }

    public void ApplyTo(Profile p)
    {
        p.Page.MarginsMm = new Margins { Left = MarginLeft.Value, Right = MarginRight.Value, Top = MarginTop.Value, Bottom = MarginBottom.Value };
        p.Page.Numbering.Align = PageNumberAlign;
        // Заголовки и подписи того же кегля, что и текст, меняются вместе с ним
        var oldSize = p.Text.SizePt;
        var size = FontSize.Value;
        if (Math.Abs(oldSize - size) > 0.01)
        {
            foreach (var h in new[] { p.Headings.Structural }.Concat(p.Headings.Levels))
                if (Math.Abs(h.SizePt - oldSize) < 0.01) h.SizePt = size;
            foreach (var c in new[] { p.Captions.Figure, p.Captions.Table })
                if (Math.Abs(c.SizePt - oldSize) < 0.01) c.SizePt = size;
        }
        if (!string.IsNullOrWhiteSpace(Font)) p.Text.Font = Font.Trim();
        p.Text.SizePt = size;
        p.Text.LineSpacing = LineSpacing;
        p.Text.FirstLineIndentCm = Indent.Value;
        p.Text.Align = TextAlign;
        p.Text.Hyphenation = Hyphenation;
        foreach (var h in p.Headings.Levels) h.Align = HeadingAlign;
        p.Headings.Level1.NewPage = ChapterNewPage;
        p.Headings.Structural.Case = StructuralUpper ? "upper" : "as_is";
        p.Headings.TocLevels = TocLevels;
        p.Captions.Figure.Prefix = FigurePrefix;
        p.Captions.Separator = Separator;
        p.Captions.Table.Align = TableCaptionAlign;
        p.Captions.TableTextSizePt = TableFontSize.Value;
    }

    // ------------------------------------------------------------ отличия от эталона

    void UpdateDiffs()
    {
        var b = _baseline;
        var m = b.Page.MarginsMm;
        string Num(double v) => NumberRule.Format(v);
        string OnOff(bool v) => v ? "вкл." : "откл.";
        string Label(Choice[] list, object v) => (list.FirstOrDefault(c => Equals(c.Value, v))?.Label ?? v.ToString()!).ToLowerInvariant();

        Diff("margins", m.Left != MarginLeft.Value || m.Right != MarginRight.Value || m.Top != MarginTop.Value || m.Bottom != MarginBottom.Value,
            $"{Num(m.Left)} / {Num(m.Right)} / {Num(m.Top)} / {Num(m.Bottom)} мм");
        Diff("pageNumber", b.Page.Numbering.Align != PageNumberAlign, Label(PageNumberAligns, b.Page.Numbering.Align));
        Diff("font", !string.Equals(b.Text.Font, Font.Trim(), StringComparison.OrdinalIgnoreCase), b.Text.Font);
        Diff("fontSize", b.Text.SizePt != FontSize.Value, $"{Num(b.Text.SizePt)} пт");
        Diff("lineSpacing", Math.Abs(b.Text.LineSpacing - LineSpacing) > 0.001, Num(b.Text.LineSpacing));
        Diff("indent", Math.Abs(b.Text.FirstLineIndentCm - Indent.Value) > 0.001, $"{Num(b.Text.FirstLineIndentCm)} см");
        Diff("align", b.Text.Align != TextAlign, Label(TextAligns, b.Text.Align));
        Diff("hyphenation", b.Text.Hyphenation != Hyphenation, OnOff(b.Text.Hyphenation));
        Diff("headingAlign", b.Headings.Level1.Align != HeadingAlign, Label(HeadingAligns, b.Headings.Level1.Align));
        Diff("chapterNewPage", b.Headings.Level1.NewPage != ChapterNewPage, OnOff(b.Headings.Level1.NewPage));
        Diff("structuralUpper", (b.Headings.Structural.Case == "upper") != StructuralUpper, OnOff(b.Headings.Structural.Case == "upper"));
        Diff("tocLevels", b.Headings.TocLevels != TocLevels, b.Headings.TocLevels.ToString());
        Diff("figurePrefix", b.Captions.Figure.Prefix != FigurePrefix, b.Captions.Figure.Prefix);
        Diff("separator", b.Captions.Separator != Separator, Label(Separators, b.Captions.Separator));
        Diff("tableCaptionAlign", b.Captions.Table.Align != TableCaptionAlign, Label(TableCaptionAligns, b.Captions.Table.Align));
        Diff("tableFontSize", b.Captions.TableTextSizePt != TableFontSize.Value, $"{Num(b.Captions.TableTextSizePt)} пт");

        OnPropertyChanged(nameof(ChangedCount));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(NavSubtitle));
        OnPropertyChanged(nameof(MarginsWarning));
        OnPropertyChanged(nameof(TableFontWarning));
        OnPropertyChanged(nameof(SeparatorExample));
    }

    void Diff(string key, bool changed, string baselineText)
    {
        var d = _diffs[key];
        d.IsChanged = changed;
        var by = _baselineGost != null ? $"по ГОСТ: {baselineText}" : $"в профиле: {baselineText}";
        d.ChangedText = _fromSample.TryGetValue(key, out var conf)
            ? conf == Confidence.Low ? $"Из примера · {by} · определено неуверенно — проверьте" : $"Из примера · {by}"
            : $"Изменено · {by}";
    }

    // ------------------------------------------------------------ найденные по примеру правила (DESIGN.md 17)

    public IReadOnlyCollection<string> FromSampleKeys => _fromSample.Keys;
    public void ClearSampleMarks() { _fromSample.Clear(); UpdateDiffs(); }
    public void RestoreSampleMarks(IReadOnlyDictionary<string, Confidence> marks)
    {
        _fromSample.Clear();
        foreach (var (k, v) in marks) _fromSample[k] = v;
        UpdateDiffs();
    }
    public IReadOnlyDictionary<string, Confidence> SampleMarks => new Dictionary<string, Confidence>(_fromSample);

    /// <summary>Текущее значение правила в виде, как в результате определения.</summary>
    public string CurrentDisplay(string key) => Core.Detection.Rules.Display(key, CurrentValue(key));

    object CurrentValue(string key) => key switch
    {
        "margins" => new MarginsValue(MarginLeft.Value, MarginRight.Value, MarginTop.Value, MarginBottom.Value),
        "pageNumber" => PageNumberAlign, "font" => Font, "fontSize" => FontSize.Value, "lineSpacing" => LineSpacing,
        "indent" => Indent.Value, "align" => TextAlign, "hyphenation" => Hyphenation, "headingAlign" => HeadingAlign,
        "chapterNewPage" => ChapterNewPage, "structuralUpper" => StructuralUpper, "tocLevels" => TocLevels,
        "figurePrefix" => FigurePrefix, "separator" => Separator, "tableCaptionAlign" => TableCaptionAlign,
        "tableFontSize" => TableFontSize.Value, _ => "",
    };

    /// <summary>Найденное значение уже совпадает с текущим («уже так»).</summary>
    public bool IsSame(DetectedRule rule)
    {
        var current = CurrentValue(rule.Key);
        return (rule.Value, current) switch
        {
            (MarginsValue f, MarginsValue c) => (f.Left is not { } l || Math.Abs(l - c.Left!.Value) < 0.5)
                && (f.Right is not { } r || Math.Abs(r - c.Right!.Value) < 0.5) && (f.Top is not { } t || Math.Abs(t - c.Top!.Value) < 0.5)
                && (f.Bottom is not { } b || Math.Abs(b - c.Bottom!.Value) < 0.5),
            (double f, double c) => Math.Abs(f - c) < 0.01,
            (int f, int c) => f == c,
            (string f, string c) => string.Equals(f, c, StringComparison.OrdinalIgnoreCase),
            (bool f, bool c) => f == c,
            _ => false,
        };
    }

    /// <summary>Записывает найденные значения в правила так же, как ручное редактирование, и помечает их «Из примера».</summary>
    public void ApplyDetected(IEnumerable<DetectedRule> rules)
    {
        _applyingSample = true;
        try
        {
            foreach (var rule in rules)
            {
                switch (rule.Key, rule.Value)
                {
                    case ("margins", MarginsValue m):
                        if (m.Left is { } l) MarginLeft.Text = NumberRule.Format(l);
                        if (m.Right is { } r) MarginRight.Text = NumberRule.Format(r);
                        if (m.Top is { } t) MarginTop.Text = NumberRule.Format(t);
                        if (m.Bottom is { } b) MarginBottom.Text = NumberRule.Format(b);
                        break;
                    case ("pageNumber", string v): PageNumberAlign = v; break;
                    case ("font", string v): Font = v; break;
                    case ("fontSize", double v): FontSize.Text = NumberRule.Format(Math.Clamp(v, 10, 16)); break;
                    case ("lineSpacing", double v): LineSpacing = v; break;
                    case ("indent", double v): Indent.Text = NumberRule.Format(Math.Clamp(v, 0, 2.5)); break;
                    case ("align", string v): TextAlign = v; break;
                    case ("hyphenation", bool v): Hyphenation = v; break;
                    case ("headingAlign", string v): HeadingAlign = v; break;
                    case ("chapterNewPage", bool v): ChapterNewPage = v; break;
                    case ("structuralUpper", bool v): StructuralUpper = v; break;
                    case ("tocLevels", int v): TocLevels = Math.Clamp(v, 1, 3); break;
                    case ("figurePrefix", string v): FigurePrefix = v; break;
                    case ("separator", string v): Separator = v; break;
                    case ("tableCaptionAlign", string v): TableCaptionAlign = v; break;
                    case ("tableFontSize", double v): TableFontSize.Text = NumberRule.Format(Math.Clamp(v, 10, 14)); break;
                }
                _fromSample[rule.Key] = rule.Confidence;
            }
        }
        finally
        {
            _applyingSample = false;
        }
        UpdateDiffs();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Revert(string key)
    {
        var b = _baseline;
        switch (key)
        {
            case "margins":
                MarginLeft.Text = NumberRule.Format(b.Page.MarginsMm.Left);
                MarginRight.Text = NumberRule.Format(b.Page.MarginsMm.Right);
                MarginTop.Text = NumberRule.Format(b.Page.MarginsMm.Top);
                MarginBottom.Text = NumberRule.Format(b.Page.MarginsMm.Bottom);
                break;
            case "pageNumber": PageNumberAlign = b.Page.Numbering.Align; break;
            case "font": Font = b.Text.Font; break;
            case "fontSize": FontSize.Text = NumberRule.Format(b.Text.SizePt); break;
            case "lineSpacing": LineSpacing = b.Text.LineSpacing; break;
            case "indent": Indent.Text = NumberRule.Format(b.Text.FirstLineIndentCm); break;
            case "align": TextAlign = b.Text.Align; break;
            case "hyphenation": Hyphenation = b.Text.Hyphenation; break;
            case "headingAlign": HeadingAlign = b.Headings.Level1.Align; break;
            case "chapterNewPage": ChapterNewPage = b.Headings.Level1.NewPage; break;
            case "structuralUpper": StructuralUpper = b.Headings.Structural.Case == "upper"; break;
            case "tocLevels": TocLevels = b.Headings.TocLevels; break;
            case "figurePrefix": FigurePrefix = b.Captions.Figure.Prefix; break;
            case "separator": Separator = b.Captions.Separator; break;
            case "tableCaptionAlign": TableCaptionAlign = b.Captions.Table.Align; break;
            case "tableFontSize": TableFontSize.Text = NumberRule.Format(b.Captions.TableTextSizePt); break;
        }
        Reverted?.Invoke(key);
    }

    /// <summary>Свойство, где первая ошибка (для перехода к ней).</summary>
    public string? FirstErrorProperty =>
        MarginLeft.HasErrors ? nameof(MarginLeft) : MarginRight.HasErrors ? nameof(MarginRight)
        : MarginTop.HasErrors ? nameof(MarginTop) : MarginBottom.HasErrors ? nameof(MarginBottom)
        : _fontError != null ? nameof(Font) : FontSize.HasErrors ? nameof(FontSize)
        : Indent.HasErrors ? nameof(Indent) : TableFontSize.HasErrors ? nameof(TableFontSize) : null;

    void ManualChange(string key)
    {
        if (!_applyingSample && !_loading) _fromSample.Remove(key);
    }

    bool SetRule<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return false;
        if (name != null && PropertyKeys.TryGetValue(name, out var key)) ManualChange(key);
        if (name is nameof(FigurePrefix) or nameof(Separator)) OnPropertyChanged(nameof(SeparatorExample));
        OnChanged();
        return true;
    }

    void OnChanged(bool typing = false)
    {
        // При применении найденных правил событие одно — в конце ApplyDetected
        if (_loading || _applyingSample) return;
        UpdateDiffs();
        if (typing) TypingChanged?.Invoke(this, EventArgs.Empty);
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Изменение числа при наборе — предпросмотр обновляется с задержкой (DESIGN.md 8).</summary>
    public event EventHandler? TypingChanged;

    void RaiseErrors(string? property = null)
    {
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(NavSubtitle));
        OnPropertyChanged(nameof(MarginsError));
        OnPropertyChanged(nameof(MarginsWarning));
        OnPropertyChanged(nameof(TableFontWarning));
    }
}
