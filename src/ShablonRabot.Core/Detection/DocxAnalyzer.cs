using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ShablonRabot.Core.Detection;

/// <summary>
/// Определяет правила оформления по образцу работы (.docx): итоговое форматирование абзацев с учётом
/// настроек по умолчанию, цепочки стилей и прямого форматирования; значение правила — по большинству текста.
/// </summary>
public static partial class DocxAnalyzer
{
    [GeneratedRegex(@"^\s*(Рисунок|Рис\.)\s*[А-ЯA-Z]?\.?\s*\d+(?:[.\-]\d+)*\s*([–—\-]|\.)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex FigureCaption();

    [GeneratedRegex(@"^\s*Таблица\s+[А-ЯA-Z]?\.?\s*\d+(?:[.\-]\d+)*", RegexOptions.IgnoreCase)]
    private static partial Regex TableCaption();

    [GeneratedRegex(@"^(введение|заключение|содержание|оглавление|список (использованных|литературы|источников)|библиографический список|приложение|реферат|аннотация|перечень сокращений)", RegexOptions.IgnoreCase)]
    private static partial Regex StructuralTitle();

    [GeneratedRegex(@"^\s*(глава\s+)?\d+(\.\d+)*\.?\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedHeading();

    [GeneratedRegex(@"\\o\s*""?(\d)\s*-\s*(\d)")]
    private static partial Regex TocLevels();

    const double TwipsPerMm = 56.6929, TwipsPerCm = 566.929;

    public static RulesDetection Analyze(string path, string? displayName = null, bool approximate = false)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("В файле нет основного документа.");
        var body = main.Document?.Body ?? throw new InvalidDataException("Документ пуст.");
        var ctx = new Resolver(main);
        var result = new RulesDetection { Source = displayName ?? Path.GetFileName(path), Approximate = approximate };

        var paragraphs = body.Descendants<Paragraph>().ToList();
        var inTables = paragraphs.Where(p => p.Ancestors<Table>().Any()).ToHashSet();
        var textOf = paragraphs.ToDictionary(p => p, p => string.Concat(p.Descendants<Text>().Select(t => t.Text)).Trim());

        // Основной текст: длинные абзацы вне таблиц, не заголовки, не содержание и не списки
        var bodyParas = paragraphs.Where(p => !inTables.Contains(p) && textOf[p].Length >= 100
                                              && ctx.OutlineLevel(p) is null && !ctx.IsTocStyle(p) && !ctx.IsList(p)).ToList();
        if (bodyParas.Count == 0)
            bodyParas = paragraphs.Where(p => !inTables.Contains(p) && textOf[p].Length >= 40 && ctx.OutlineLevel(p) is null).ToList();
        double Weight(Paragraph p) => textOf[p].Length;
        var totalWeight = bodyParas.Sum(Weight);

        DetectMargins(body, paragraphs, textOf, result);
        DetectPageNumber(main, body, ctx, result);

        if (bodyParas.Count > 0)
        {
            // Шрифт и размер — по длине текста в прогонах
            var runs = bodyParas.SelectMany(p => p.Elements<Run>().Select(r => (p, r))).ToList();
            var fonts = runs.Select(x => (Key: ctx.Font(x.p, x.r), W: (double)x.r.InnerText.Length)).Where(x => x.Key != null);
            Majority(result, "font", fonts.Select(x => (x.Key!, x.W)), share => $"В {share:P0} основного текста", v => v);
            var sizes = runs.Select(x => (Key: ctx.FontSize(x.p, x.r), W: (double)x.r.InnerText.Length)).Where(x => x.Key != null);
            Majority(result, "fontSize", sizes.Select(x => (x.Key!.Value, x.W)), share => $"В {share:P0} основного текста", v => v);

            Majority(result, "lineSpacing", bodyParas.Select(p => (ctx.LineSpacing(p), Weight(p))), share => $"В {share:P0} абзацев основного текста", v => v);
            Majority(result, "indent", bodyParas.Select(p => (Math.Round(ctx.FirstLineIndentCm(p) * 20) / 20, Weight(p))),
                share => $"В {share:P0} абзацев основного текста", v => v);
            Majority(result, "align", bodyParas.Select(p => (ctx.Justification(p) is "both" or "distribute" ? "justify" : "left", Weight(p))),
                share => $"В {share:P0} абзацев основного текста", v => v);
        }

        var hyph = main.DocumentSettingsPart?.Settings?.GetFirstChild<AutoHyphenation>();
        result.Add("hyphenation", hyph != null && (hyph.Val == null || hyph.Val.Value), Confidence.Medium, "Настройки документа");

        DetectHeadings(paragraphs, inTables, textOf, ctx, result);
        DetectToc(body, result);
        DetectCaptions(paragraphs, textOf, ctx, result);

        // Шрифт в таблицах
        var tableRuns = ContentTables(body, paragraphs, textOf, ctx).SelectMany(t => t.Descendants<Paragraph>())
            .SelectMany(p => p.Elements<Run>().Select(r => (Key: ctx.FontSize(p, r), W: (double)r.InnerText.Length)))
            .Where(x => x.Key != null && x.W > 0).ToList();
        if (tableRuns.Sum(x => x.W) >= 30)
            Majority(result, "tableFontSize", tableRuns.Select(x => (x.Key!.Value, x.W)), share => $"В {share:P0} текста таблиц", v => v);

        if (approximate)
            for (var i = 0; i < result.Rules.Count; i++)
                if (result.Rules[i].Confidence == Confidence.High)
                    result.Rules[i] = result.Rules[i] with { Confidence = Confidence.Medium };

        result.Verdict = Standards.Match(result);
        return result;
    }

    /// <summary>Значение по большинству: уверенность зависит от доли (≥ 70 % — высокая, ≥ 45 % — средняя).</summary>
    static void Majority<T>(RulesDetection result, string key, IEnumerable<(T Value, double Weight)> items, Func<double, string> evidence,
                            Func<T, object> convert) where T : notnull
    {
        var groups = items.Where(i => i.Weight > 0).GroupBy(i => i.Value).Select(g => (g.Key, W: g.Sum(x => x.Weight), N: g.Count()))
            .OrderByDescending(g => g.W).ToList();
        if (groups.Count == 0) return;
        var total = groups.Sum(g => g.W);
        var top = groups[0];
        var share = top.W / total;
        var confidence = share >= 0.7 && top.N >= 2 ? Confidence.High : share >= 0.45 ? Confidence.Medium : Confidence.Low;
        result.Add(key, convert(top.Key), confidence, evidence(share));
    }

    /// <summary>
    /// Таблицы с содержимым работы: без таблиц титульного листа (до первого разрыва раздела или заголовка «Содержание»,
    /// например таблицы подписей). Если у части таблиц есть подпись «Таблица N», берутся только они.
    /// </summary>
    static List<Table> ContentTables(Body body, List<Paragraph> paragraphs, Dictionary<Paragraph, string> textOf, Resolver ctx)
    {
        var order = paragraphs.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var frontEnd = paragraphs.FindIndex(p => !p.Ancestors<Table>().Any()
            && (p.ParagraphProperties?.SectionProperties != null || ctx.IsTocStyle(p)
                || Regex.IsMatch(textOf[p], @"^(содержание|оглавление)$", RegexOptions.IgnoreCase)));
        var tables = body.Descendants<Table>().Where(t => t.Ancestors<Table>().FirstOrDefault() is null)
            .Where(t => frontEnd < 0 || t.Descendants<Paragraph>().FirstOrDefault() is not { } first || order[first] > frontEnd)
            .ToList();

        bool IsCaption(Paragraph? p) => p != null && (TableCaption().IsMatch(textOf[p])
            || textOf[p].StartsWith("Продолжение таблицы", StringComparison.OrdinalIgnoreCase));
        Paragraph? Near(OpenXmlElement t, bool before)
        {
            for (var e = before ? t.PreviousSibling() : t.NextSibling(); e != null; e = before ? e.PreviousSibling() : e.NextSibling())
                if (e is Paragraph p && textOf.GetValueOrDefault(p, "").Length > 0) return p;
                else if (e is not Paragraph) return null;
            return null;
        }
        var captioned = tables.Where(t => IsCaption(Near(t, true)) || IsCaption(Near(t, false))).ToList();
        return captioned.Count > 0 ? captioned : tables;
    }

    // ------------------------------------------------------------ страница

    static void DetectMargins(Body body, List<Paragraph> paragraphs, Dictionary<Paragraph, string> textOf, RulesDetection result)
    {
        // Раздел с наибольшим количеством текста (титульный лист иногда в отдельном разделе)
        var sections = new List<(SectionProperties Sect, int Chars)>();
        var chars = 0;
        foreach (var p in body.Elements<Paragraph>())
        {
            chars += textOf.GetValueOrDefault(p, "").Length;
            if (p.ParagraphProperties?.SectionProperties is { } sp) { sections.Add((sp, chars)); chars = 0; }
        }
        if (body.GetFirstChild<SectionProperties>() is { } last) sections.Add((last, chars + body.Elements<Table>().Sum(t => t.InnerText.Length)));
        var main = sections.OrderByDescending(s => s.Chars).Select(s => s.Sect).FirstOrDefault();
        if (main?.GetFirstChild<PageMargin>() is not { } m) return;
        double Mm(double twips) => Math.Round(twips / TwipsPerMm);
        var left = Mm((m.Left?.Value ?? 0) + (m.Gutter?.Value ?? 0));
        result.Add("margins", new MarginsValue(left, Mm(m.Right?.Value ?? 0), Mm(Math.Abs(m.Top?.Value ?? 0)), Mm(Math.Abs(m.Bottom?.Value ?? 0))),
            Confidence.High, "Параметры страницы");
    }

    static void DetectPageNumber(MainDocumentPart main, Body body, Resolver ctx, RulesDetection result)
    {
        var sect = body.GetFirstChild<SectionProperties>();
        var ids = sect?.Elements<FooterReference>().Select(f => (f.Id?.Value, top: false))
            .Concat(sect.Elements<HeaderReference>().Select(h => (h.Id?.Value, top: true))).ToList() ?? [];
        foreach (var (id, top) in ids)
        {
            if (id == null || main.GetPartById(id) is not OpenXmlPart part || part.RootElement is null) continue;
            foreach (var p in part.RootElement.Descendants<Paragraph>())
            {
                var hasPage = p.Descendants<FieldCode>().Any(f => f.Text.Contains("PAGE", StringComparison.OrdinalIgnoreCase))
                              || p.Descendants<SimpleField>().Any(f => f.Instruction?.Value?.Contains("PAGE", StringComparison.OrdinalIgnoreCase) == true);
                if (!hasPage) continue;
                var jc = ctx.Justification(p);
                var horizontal = jc is "right" or "end" ? "right" : "center";
                var value = top ? $"top-{horizontal}" : horizontal;
                result.Add("pageNumber", value, Confidence.High, top ? $"Верхний колонтитул: номер {(horizontal == "right" ? "справа" : "по центру")}" : $"Колонтитул: номер {(horizontal == "right" ? "справа" : "по центру")}");
                return;
            }
        }
    }

    // ------------------------------------------------------------ заголовки, содержание, подписи

    static void DetectHeadings(List<Paragraph> paragraphs, HashSet<Paragraph> inTables, Dictionary<Paragraph, string> textOf, Resolver ctx, RulesDetection result)
    {
        var structural = new List<Paragraph>();
        var chapters = new List<Paragraph>();
        foreach (var p in paragraphs)
        {
            if (inTables.Contains(p) || ctx.IsTocStyle(p)) continue;
            var text = textOf[p];
            if (text.Length == 0 || text.Length > 150) continue;
            var level = ctx.OutlineLevel(p);
            var looksHeading = level is 0 || (level is null && ctx.IsBold(p) && text.Length < 120);
            if (StructuralTitle().IsMatch(text) && text.Length < 80 && (looksHeading || text.Length < 60) && !text.TrimEnd().EndsWith('.'))
                structural.Add(p);
            else if ((level is 0 || (looksHeading && NumberedHeading().IsMatch(text))) && !StructuralTitle().IsMatch(text))
                chapters.Add(p);
        }

        if (structural.Count >= 2)
        {
            var upper = structural.Count(p => textOf[p] == textOf[p].ToUpperInvariant() || ctx.IsCaps(p));
            var share = (double)upper / structural.Count;
            result.Add("structuralUpper", share >= 0.5, share is >= 0.8 or <= 0.2 ? Confidence.High : Confidence.Medium,
                $"{Rules.Plural(structural.Count, "структурный заголовок", "структурных заголовка", "структурных заголовков")} (введение, заключение…)");
        }

        if (chapters.Count >= 2)
        {
            var aligns = chapters.Select(p => ctx.Justification(p) == "center" ? "center" : "indent").ToList();
            var centered = aligns.Count(a => a == "center");
            var value = centered * 2 >= aligns.Count ? "center" : "indent";
            var share = (double)aligns.Count(a => a == value) / aligns.Count;
            result.Add("headingAlign", value, share >= 0.8 ? Confidence.High : Confidence.Medium, $"{Rules.Plural(chapters.Count, "заголовок", "заголовка", "заголовков")} глав и параграфов");

            // Главы с новой страницы: заголовки 1-го уровня, перед которыми разрыв страницы
            var top = chapters.Where(p => ctx.OutlineLevel(p) is 0 || Regex.IsMatch(textOf[p], @"^\s*(глава\s+)?\d+\.?\s", RegexOptions.IgnoreCase)).ToList();
            if (top.Count >= 2)
            {
                var broken = top.Count(p => ctx.PageBreakBefore(p) || BreakBefore(p));
                var s = (double)broken / top.Count;
                result.Add("chapterNewPage", s >= 0.5, s is >= 0.8 or <= 0.2 ? Confidence.High : Confidence.Medium, $"{Rules.Plural(top.Count, "заголовок", "заголовка", "заголовков")} глав");
            }
        }
    }

    /// <summary>Разрыв страницы в конце предыдущего абзаца или в начале этого.</summary>
    static bool BreakBefore(Paragraph p)
    {
        if (p.Elements<Run>().FirstOrDefault()?.Elements<Break>().Any(b => b.Type?.Value == BreakValues.Page) == true) return true;
        var prev = p.PreviousSibling<Paragraph>();
        if (prev == null) return false;
        if (prev.ParagraphProperties?.SectionProperties != null) return true;
        return prev.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page)
               || prev.Descendants<LastRenderedPageBreak>().Any() && string.IsNullOrWhiteSpace(prev.InnerText);
    }

    static void DetectToc(Body body, RulesDetection result)
    {
        var instr = body.Descendants<FieldCode>().Select(f => f.Text).FirstOrDefault(t => t.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase))
                    ?? body.Descendants<SimpleField>().Select(f => f.Instruction?.Value).FirstOrDefault(t => t?.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase) == true);
        if (instr != null && TocLevels().Match(instr) is { Success: true } m && int.TryParse(m.Groups[2].Value, out var to))
            result.Add("tocLevels", Math.Clamp(to, 1, 3), Confidence.High, $"Автособираемое содержание, уровни 1–{Math.Clamp(to, 1, 3)}");
    }

    static void DetectCaptions(List<Paragraph> paragraphs, Dictionary<Paragraph, string> textOf, Resolver ctx, RulesDetection result)
    {
        var figures = paragraphs.Select(p => (p, m: FigureCaption().Match(textOf[p]))).Where(x => x.m.Success && textOf[x.p].Length < 300).ToList();
        if (figures.Count > 0)
        {
            var prefixes = figures.Select(f => f.m.Groups[1].Value.StartsWith("Рис.", StringComparison.OrdinalIgnoreCase) ? "Рис." : "Рисунок").ToList();
            var prefix = prefixes.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
            result.Add("figurePrefix", prefix, figures.Count >= 2 ? Confidence.High : Confidence.Medium, $"{Rules.Plural(figures.Count, "подпись", "подписи", "подписей")} рисунков");
            var seps = figures.Select(f => f.m.Groups[2].Value).Where(s => s.Length > 0).Select(s => s == "." ? ". " : " – ").ToList();
            if (seps.Count > 0)
            {
                var sep = seps.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
                result.Add("separator", sep, seps.Count >= 2 ? Confidence.High : Confidence.Medium, $"{Rules.Plural(seps.Count, "подпись", "подписи", "подписей")} рисунков");
            }
        }

        var tables = paragraphs.Where(p => TableCaption().IsMatch(textOf[p]) && textOf[p].Length < 300).ToList();
        if (tables.Count > 0)
        {
            var centered = tables.Count(p => ctx.Justification(p) == "center");
            var value = centered * 2 > tables.Count ? "center" : "left";
            result.Add("tableCaptionAlign", value, tables.Count >= 2 ? Confidence.High : Confidence.Medium, $"{Rules.Plural(tables.Count, "подпись", "подписи", "подписей")} таблиц");
        }
    }

    // ------------------------------------------------------------ итоговое форматирование

    /// <summary>Свойство абзаца/прогона: прямое форматирование → стиль и его родители → настройки по умолчанию.</summary>
    sealed class Resolver
    {
        readonly Dictionary<string, Style> _styles;
        readonly string? _defaultParaStyle;
        readonly OpenXmlElement? _pDefault, _rDefault;
        readonly string? _minorFont, _majorFont;

        public Resolver(MainDocumentPart main)
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
    }
}
