using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ShablonRabot.Core.Detection;

namespace ShablonRabot.Core.Checking;

/// <summary>
/// Проверка готовой работы (.docx) на соответствие правилам вида работы: страница, текст, заголовки, содержание,
/// рисунки и таблицы, список литературы (ГОСТ Р 7.0.100, 7.0.5, 7.0.108), приложения. Документ не изменяется.
/// </summary>
public static partial class DocumentChecker
{
    [GeneratedRegex(@"^\s*(Рисунок|Рис\.)\s*([А-ЯA-Z]\.?)?\s*(\d+(?:\.\d+)*)\s*([–—\-]|\.)?\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FigureCaption();

    [GeneratedRegex(@"^\s*Таблица\s+([А-ЯA-Z]\.?)?\s*(\d+(?:\.\d+)*)\s*([–—\-]|\.)?\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TableCaption();

    [GeneratedRegex(@"^(введение|заключение|содержание|оглавление|список (использованных|литературы|источников)|библиографический список|реферат|аннотация|перечень сокращений|термины и определения|список сокращений)", RegexOptions.IgnoreCase)]
    private static partial Regex StructuralTitle();

    [GeneratedRegex(@"^(список (использованных|литературы|источников)|библиографический список|список использованной литературы)", RegexOptions.IgnoreCase)]
    private static partial Regex ReferencesTitle();

    [GeneratedRegex(@"^приложение\s+([А-ЯЁA-Z])\b", RegexOptions.IgnoreCase)]
    private static partial Regex AppendixTitle();

    [GeneratedRegex(@"^\s*(глава\s+)?(\d+(\.\d+)*)\.?\s+\S", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedHeading();

    [GeneratedRegex(@"рис(?:ун\w*|\.)\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex FigureReference();

    [GeneratedRegex(@"табл(?:иц\w*|\.)\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex TableReference();

    [GeneratedRegex(@"\[([\d\s,;–\-]+)(?:[,;]?\s*(?:с|c|л|s|p)\.\s*[\d\s–\-,]+)?\]")]
    private static partial Regex Citation();

    const int MaxLocations = 40;

    sealed record Para(int Index, Paragraph P, string Text, bool InTable)
    {
        public string Section { get; set; } = "";
        public Kind Kind { get; set; } = Kind.Body;
    }

    enum Kind { Title, Toc, Heading, Structural, Caption, Body, Reference, Empty, Other }

    /// <summary>
    /// Готовит .docx для проверки: .docx — как есть; .doc, .rtf и .odt — копия, которую сохраняет Word (IsTemp: удалить после).
    /// </summary>
    /// <exception cref="DetectionException"/>
    public static (string Docx, bool IsTemp) PrepareDocx(string path, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf" || !SampleAnalyzer.IsSupported(path)) throw new DetectionException(DetectionError.Unreadable, name);
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (IOException e) when (e is not FileNotFoundException) { throw new DetectionException(DetectionError.Locked, name, e); }
        catch (Exception e) when (e is UnauthorizedAccessException or FileNotFoundException) { throw new DetectionException(DetectionError.Unreadable, name, e); }
        if (!SampleAnalyzer.WordExtensions.Contains(ext)) return (path, false);
        if (!TemplateService.IsWordInstalled) throw new DetectionException(DetectionError.NeedsWord, name);
        return (SampleAnalyzer.ConvertWithWord(path, name, ct), true);
    }

    /// <summary>Проверяет файл: .docx напрямую; .doc, .rtf и .odt — через копию, которую сохраняет Word.</summary>
    /// <exception cref="DetectionException"/>
    public static CheckReport CheckFile(string path, Profile rules, string rulesName, CancellationToken ct = default)
    {
        var (docx, isTemp) = PrepareDocx(path, ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return CheckDocx(docx, rules, rulesName, Path.GetFileName(path));
        }
        finally
        {
            if (isTemp) try { File.Delete(docx); } catch (IOException) { }
        }
    }

    /// <summary>Проверка подготовленного .docx; ошибки чтения — как DetectionException.</summary>
    /// <exception cref="DetectionException"/>
    public static CheckReport CheckDocx(string docx, Profile rules, string rulesName, string displayName)
    {
        try { return Check(docx, rules, rulesName, displayName); }
        catch (Exception e) when (e is not OperationCanceledException and not DetectionException)
        {
            throw new DetectionException(DetectionError.Unreadable, displayName, e);
        }
    }

    public static CheckReport Check(string docxPath, Profile rules, string rulesName, string? displayName = null)
    {
        using var doc = WordprocessingDocument.Open(docxPath, false);
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("В файле нет основного документа.");
        var body = main.Document?.Body ?? throw new InvalidDataException("Документ пуст.");
        var ctx = new FormatResolver(main);
        var report = new CheckReport { FileName = displayName ?? Path.GetFileName(docxPath), RulesName = rulesName };

        var paras = body.Descendants<Paragraph>()
            .Select((p, i) => new Para(i, p, string.Concat(p.Descendants<Text>().Select(t => t.Text)), p.Ancestors<Table>().Any()))
            .ToList();
        report.ParagraphCount = paras.Count;
        Classify(paras, body, ctx);
        report.BodyParagraphCount = paras.Count(p => p.Kind == Kind.Body);

        var c = new Checker(report, rules, ctx, paras);
        c.Page(main, body);
        c.Text();
        c.Headings();
        c.Toc(body);
        c.Captions(body);
        c.References();
        c.Appendices();
        // Сначала ошибки, затем замечания и советы; внутри — по количеству мест
        var ordered = report.Issues.OrderBy(i => i.Severity).ThenByDescending(i => i.Count).ToList();
        report.Issues.Clear();
        report.Issues.AddRange(ordered);
        return report;
    }

    /// <summary>Роли абзацев: титульный лист, содержание, заголовки, подписи, основной текст, список литературы.</summary>
    static void Classify(List<Para> paras, Body body, FormatResolver ctx)
    {
        // Титульный лист — до содержания, первого разрыва раздела или первого заголовка со стилем.
        // Слова «РЕФЕРАТ», «ОТЧЁТ» на титуле — не структурные элементы, поэтому по тексту границу не ищем.
        var candidates = new List<int>();
        var toc = paras.FindIndex(x => !x.InTable && (ctx.IsTocStyle(x.P) || IsTocField(x.P)
            || Regex.IsMatch(x.Text.Trim(), @"^(содержание|оглавление)$", RegexOptions.IgnoreCase)));
        if (toc >= 0) candidates.Add(toc);
        var firstSection = paras.FindIndex(x => x.P.ParagraphProperties?.SectionProperties != null);
        if (firstSection >= 0) candidates.Add(firstSection + 1);
        var firstHeading = paras.FindIndex(x => !x.InTable && ctx.OutlineLevel(x.P) is 0);
        if (firstHeading >= 0) candidates.Add(firstHeading);
        var frontEnd = candidates.Count > 0 ? candidates.Min() : 0;
        // Без содержания и стилей: титул — до первого разрыва страницы, если он в начале документа
        if (candidates.Count == 0)
        {
            var br = paras.FindIndex(x => x.P.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page) || ctx.PageBreakBefore(x.P));
            if (br is > 0 and < 60) frontEnd = ctx.PageBreakBefore(paras[br].P) ? br : br + 1;
        }

        var tocAnchors = paras.Where(x => x.P.Descendants<Hyperlink>().Any(h => h.Anchor?.Value?.StartsWith("_Toc") == true)).Select(x => x.Index).ToHashSet();
        var section = "";
        var inReferences = false;
        foreach (var x in paras)
        {
            var text = x.Text.Trim();
            if (x.Index < frontEnd) { x.Kind = Kind.Title; x.Section = "Титульный лист"; continue; }
            if (text.Length == 0)
            {
                x.Kind = x.P.Descendants<Drawing>().Any() || x.P.Descendants<DocumentFormat.OpenXml.Math.OfficeMath>().Any() ? Kind.Other : Kind.Empty;
                x.Section = section;
                continue;
            }
            if (ctx.IsTocStyle(x.P) || tocAnchors.Contains(x.Index) || IsTocField(x.P)) { x.Kind = Kind.Toc; x.Section = "Содержание"; continue; }
            if (!x.InTable && IsStructural(x, ctx))
            {
                x.Kind = Kind.Structural;
                section = Short(text, 50);
                inReferences = ReferencesTitle().IsMatch(text);
                x.Section = section;
                continue;
            }
            if (!x.InTable && IsHeading(x, ctx))
            {
                x.Kind = Kind.Heading;
                section = Short(text, 50);
                inReferences = false;
                x.Section = section;
                continue;
            }
            x.Section = section;
            if (FigureCaption().IsMatch(text) && text.Length < 300 || TableCaption().IsMatch(text) && text.Length < 300) { x.Kind = Kind.Caption; continue; }
            if (inReferences && !x.InTable) { x.Kind = Kind.Reference; continue; }
            x.Kind = x.InTable || text.Length < 20 || x.P.Descendants<DocumentFormat.OpenXml.Math.OfficeMath>().Any() ? Kind.Other : Kind.Body;
        }

        // Пояснения к формуле: «где y – …;» и следующие строки «a – …;» пишут без абзацного отступа (ГОСТ 7.32, 6.8)
        for (var i = 0; i < paras.Count; i++)
        {
            if (paras[i].Kind != Kind.Body || !paras[i].Text.TrimStart().StartsWith("где ", StringComparison.Ordinal)) continue;
            paras[i].Kind = Kind.Other;
            for (var j = i + 1; j < paras.Count && paras[j].Kind is Kind.Body or Kind.Other && paras[j].Text.Length < 160
                 && Regex.IsMatch(paras[j].Text, @"^\s*\S{1,12}(\s*,\s*\S{1,12})*\s+[–—\-]\s"); j++)
                paras[j].Kind = Kind.Other;
        }
    }

    static bool IsTocField(Paragraph p) =>
        p.Descendants<FieldCode>().Any(f => f.Text.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase))
        || p.Descendants<SimpleField>().Any(f => f.Instruction?.Value?.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase) == true);

    static bool IsStructural(Para x, FormatResolver ctx)
    {
        var t = x.Text.Trim().TrimEnd('.');
        if (t.Length > 70) return false;
        if (AppendixTitle().IsMatch(t)) return true;
        return StructuralTitle().IsMatch(t) && (ctx.OutlineLevel(x.P) is not null || ctx.IsBold(x.P) || ctx.Justification(x.P) == "center" || t.Length < 45)
               && !t.Contains(':');
    }

    static bool IsHeading(Para x, FormatResolver ctx)
    {
        var t = x.Text.Trim();
        if (t.Length > 150) return false;
        if (ctx.OutlineLevel(x.P) is >= 0 and <= 2) return true;
        return ctx.IsBold(x.P) && t.Length < 120 && NumberedHeading().IsMatch(t) && !ctx.IsList(x.P);
    }

    static string Short(string s, int max)
    {
        s = Regex.Replace(s.Trim(), @"\s+", " ");
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    static string Num(double v) => v.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));

    // ------------------------------------------------------------ проверки

    sealed class Checker(CheckReport report, Profile rules, FormatResolver ctx, List<Para> paras)
    {
        readonly List<Para> _body = paras.Where(p => p.Kind == Kind.Body).ToList();

        CheckIssue Add(string key, string category, IssueSeverity severity, string title, string detail, string? fix, string? source,
                       IEnumerable<Para>? where = null)
        {
            // Требование из правил вида работы (не пункт ГОСТа) — называем, чьи это правила
            if (source != null && !source.StartsWith("ГОСТ")) source = $"{source} — правила «{report.RulesName}»";
            var issue = new CheckIssue { Key = key, Category = category, Severity = severity, Title = title, Detail = detail, Fix = fix, Source = source };
            var list = where?.ToList() ?? [];
            issue.Count = Math.Max(list.Count, 1);
            foreach (var p in list.Take(MaxLocations)) issue.Locations.Add(new IssueLocation(p.Index, p.Section, Short(p.Text, 70)));
            report.Issues.Add(issue);
            return issue;
        }

        void Pass(string what) => report.Passed.Add(what);

        static IEnumerable<Para> One(Para? p) => p == null ? [] : [p];

        Para? FirstContent => paras.FirstOrDefault(p => p.Kind is Kind.Body or Kind.Structural or Kind.Heading) ?? paras.FirstOrDefault();

        // ---------------- страница

        public void Page(MainDocumentPart main, Body body)
        {
            var sections = new List<(SectionProperties S, Para? First, int Chars)>();
            Para? first = null;
            var chars = 0;
            foreach (var p in paras.Where(p => !p.InTable))
            {
                first ??= p;
                chars += p.Text.Length;
                if (p.P.ParagraphProperties?.SectionProperties is { } sp) { sections.Add((sp, first, chars)); first = null; chars = 0; }
            }
            if (body.GetFirstChild<SectionProperties>() is { } last) sections.Add((last, first, chars));
            var content = sections.Where(s => s.First?.Kind != Kind.Title || s.Chars > 2000).ToList();
            if (content.Count == 0) content = sections;

            // Размер бумаги
            bool SizeOk(PageSize? ps)
            {
                if (ps == null) return true;
                var w = (ps.Width?.Value ?? 11906) / FormatResolver.TwipsPerMm;
                var h = (ps.Height?.Value ?? 16838) / FormatResolver.TwipsPerMm;
                bool Near(double a, double b) => Math.Abs(a - b) <= 2;
                return Near(w, rules.Page.WidthMm) && Near(h, rules.Page.HeightMm) || Near(w, rules.Page.HeightMm) && Near(h, rules.Page.WidthMm);
            }
            var wrongSize = content.Where(s => !SizeOk(s.S.GetFirstChild<PageSize>())).ToList();
            if (wrongSize.Count > 0)
                Add("pageSize", "Страница", IssueSeverity.Error, "Формат листа не А4", $"Нужен формат {Num(rules.Page.WidthMm)} × {Num(rules.Page.HeightMm)} мм.",
                    "Макет → Размер → A4.", "Формат А4", wrongSize.Select(s => s.First ?? FirstContent).OfType<Para>());
            else Pass("Формат листа А4");

            // Поля
            var m = rules.Page.MarginsMm;
            var badMargins = new List<(Para? P, string Found)>();
            foreach (var s in content)
            {
                if (s.S.GetFirstChild<PageMargin>() is not { } pm) continue;
                double Mm(double tw) => Math.Round(tw / FormatResolver.TwipsPerMm);
                var (l, r, t, b) = (Mm((pm.Left?.Value ?? 0) + (pm.Gutter?.Value ?? 0)), Mm(pm.Right?.Value ?? 0), Mm(Math.Abs(pm.Top?.Value ?? 0)), Mm(Math.Abs(pm.Bottom?.Value ?? 0)));
                if (Math.Abs(l - m.Left) > 1 || Math.Abs(r - m.Right) > 1 || Math.Abs(t - m.Top) > 1 || Math.Abs(b - m.Bottom) > 1)
                    badMargins.Add((s.First, $"{Num(l)} / {Num(r)} / {Num(t)} / {Num(b)}"));
            }
            if (badMargins.Count > 0)
                Add("margins", "Страница", IssueSeverity.Error, "Поля страницы не по правилам",
                    $"В документе {string.Join("; ", badMargins.Select(b => b.Found).Distinct())} мм, нужно {Num(m.Left)} / {Num(m.Right)} / {Num(m.Top)} / {Num(m.Bottom)} мм (левое / правое / верхнее / нижнее).",
                    "Макет → Поля → Настраиваемые поля.", "Поля страницы", badMargins.Select(b => b.P ?? FirstContent).OfType<Para>());
            else Pass("Поля страницы");

            // Номер страницы
            if (content.Count == 0) return;
            var sect = content.OrderByDescending(s => s.Chars).First().S;
            var found = PageNumberPosition(main, sect);
            var expected = rules.Page.Numbering.Align;
            string Where(string a) => a switch { "center" => "внизу по центру", "right" => "внизу справа", "top-center" => "вверху по центру", "top-right" => "вверху справа", _ => a };
            if (found == null)
                Add("pageNumber", "Страница", IssueSeverity.Error, "Нет номеров страниц",
                    $"Страницы нумеруют сквозной нумерацией, номер — {Where(expected)}.",
                    "Вставка → Номер страницы.", "Нумерация страниц", One(FirstContent));
            else if (found != expected)
                Add("pageNumber", "Страница", IssueSeverity.Warning, "Номер страницы не там",
                    $"Номер стоит {Where(found)}, нужно {Where(expected)}.",
                    "Двойной щелчок по колонтитулу → выровняйте номер или вставьте его заново: Вставка → Номер страницы.", "Нумерация страниц",
                    One(FirstContent));
            else Pass("Номера страниц");

            // Номер на титульном листе
            if (found != null && !rules.Page.Numbering.ShowOnTitle)
            {
                var titleSection = sections.FirstOrDefault().S;
                var titleSeparate = sections.Count > 1 && sections[0].First?.Kind == Kind.Title;
                var titlePg = titleSection?.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.TitlePage>() is { } tp && (tp.Val?.Value ?? true);
                var titleHasNumber = titleSeparate && PageNumberPosition(main, titleSection!) != null && !titlePg;
                if (!titleSeparate && !titlePg || titleHasNumber)
                    Add("titleNumber", "Страница", IssueSeverity.Warning, "Номер стоит и на титульном листе",
                        "Титульный лист входит в нумерацию, но номер на нём не ставят.",
                        "Двойной щелчок по колонтитулу → отметьте «Особый колонтитул для первой страницы».", "Нумерация страниц",
                        One(paras.FirstOrDefault(p => p.Text.Trim().Length > 0)));
            }
        }

        static string? PageNumberPosition(MainDocumentPart main, SectionProperties sect)
        {
            var refs = sect.Elements<FooterReference>().Select(f => (f.Id?.Value, top: false))
                .Concat(sect.Elements<HeaderReference>().Select(h => (h.Id?.Value, top: true)));
            foreach (var (id, top) in refs)
            {
                if (id == null || main.GetPartById(id) is not OpenXmlPart part || part.RootElement is null) continue;
                var resolver = new FormatResolver(main);
                foreach (var p in part.RootElement.Descendants<Paragraph>())
                {
                    var hasPage = p.Descendants<FieldCode>().Any(f => f.Text.Contains("PAGE", StringComparison.OrdinalIgnoreCase))
                                  || p.Descendants<SimpleField>().Any(f => f.Instruction?.Value?.Contains("PAGE", StringComparison.OrdinalIgnoreCase) == true);
                    if (!hasPage) continue;
                    var jc = resolver.Justification(p);
                    var h = jc is "right" or "end" ? "right" : jc is "left" ? "left" : "center";
                    return top ? $"top-{h}" : h;
                }
            }
            return null;
        }

        // ---------------- основной текст

        public void Text()
        {
            if (_body.Count == 0) return;
            var t = rules.Text;
            var totalChars = _body.Sum(p => p.Text.Length);

            // Шрифт и размер — по прогонам
            var fontBad = new List<Para>(); var fontChars = 0; var fontsFound = new Dictionary<string, int>();
            var sizeBad = new List<Para>(); var sizeChars = 0; var sizesFound = new Dictionary<double, int>();
            var colorBad = new List<Para>();
            foreach (var p in _body)
            {
                bool fb = false, sb = false, cb = false;
                foreach (var r in p.P.Elements<Run>())
                {
                    var len = r.InnerText.Trim().Length;
                    if (len == 0) continue;
                    var f = ctx.Font(p.P, r);
                    if (f != null && !f.Equals(t.Font, StringComparison.OrdinalIgnoreCase))
                    {
                        fb = true; fontChars += len;
                        fontsFound[f] = fontsFound.GetValueOrDefault(f) + len;
                    }
                    var s = ctx.FontSize(p.P, r);
                    if (s != null && Math.Abs(s.Value - t.SizePt) > 0.26)
                    {
                        sb = true; sizeChars += len;
                        sizesFound[s.Value] = sizesFound.GetValueOrDefault(s.Value) + len;
                    }
                    var col = ctx.Color(p.P, r);
                    if (col != null && !col.Equals("auto", StringComparison.OrdinalIgnoreCase) && !col.Equals("000000", StringComparison.OrdinalIgnoreCase)) cb = true;
                }
                if (fb) fontBad.Add(p);
                if (sb) sizeBad.Add(p);
                if (cb) colorBad.Add(p);
            }
            string Share(int chars) => totalChars == 0 ? "" : $" ({(double)chars / totalChars:P0} текста)";
            IssueSeverity Sev(int chars) => totalChars > 0 && chars * 2 >= totalChars ? IssueSeverity.Error : IssueSeverity.Warning;

            if (fontBad.Count > 0)
                Add("font", "Основной текст", Sev(fontChars), $"Шрифт не {t.Font}",
                    $"В {Plural(fontBad.Count, "абзаце", "абзацах", "абзацах")}{Share(fontChars)} шрифт {string.Join(", ", fontsFound.OrderByDescending(x => x.Value).Take(3).Select(x => x.Key))}.",
                    $"Выделите текст → шрифт {t.Font}. Лучше изменить стиль «Обычный»: правой кнопкой по стилю → Изменить.", "Шрифт", fontBad);
            else Pass($"Шрифт {t.Font}");

            if (sizeBad.Count > 0)
                Add("fontSize", "Основной текст", Sev(sizeChars), $"Размер шрифта не {Num(t.SizePt)} пт",
                    $"В {Plural(sizeBad.Count, "абзаце", "абзацах", "абзацах")}{Share(sizeChars)} размер {string.Join(", ", sizesFound.OrderByDescending(x => x.Value).Take(3).Select(x => Num(x.Key) + " пт"))}.",
                    "Выделите текст → размер шрифта. Надёжнее — в стиле «Обычный».", "Размер шрифта", sizeBad);
            else Pass($"Размер шрифта {Num(t.SizePt)} пт");

            var spacingBad = _body.Where(p => Math.Abs(ctx.LineSpacing(p.P) - t.LineSpacing) > 0.06).ToList();
            if (spacingBad.Count > 0)
                Add("lineSpacing", "Основной текст", spacingBad.Count * 2 >= _body.Count ? IssueSeverity.Error : IssueSeverity.Warning,
                    $"Межстрочный интервал не {Num(t.LineSpacing)}",
                    $"В {Plural(spacingBad.Count, "абзаце", "абзацах", "абзацах")} интервал {string.Join(", ", spacingBad.Select(p => Num(ctx.LineSpacing(p.P))).Distinct().Take(3))}.",
                    "Главная → Абзац → Междустрочный.", "Межстрочный интервал", spacingBad);
            else Pass("Межстрочный интервал");

            var indentBad = _body.Where(p => !ctx.IsList(p.P) && ctx.Justification(p.P) is not ("center" or "right")
                                             && Math.Abs(ctx.FirstLineIndentCm(p.P) - t.FirstLineIndentCm) > 0.11).ToList();
            if (indentBad.Count > 0)
                Add("indent", "Основной текст", indentBad.Count * 2 >= _body.Count ? IssueSeverity.Error : IssueSeverity.Warning,
                    $"Абзацный отступ не {Num(t.FirstLineIndentCm)} см",
                    $"В {Plural(indentBad.Count, "абзаце", "абзацах", "абзацах")} отступ {string.Join(", ", indentBad.Select(p => Num(Math.Round(ctx.FirstLineIndentCm(p.P), 2)) + " см").Distinct().Take(3))}. Отступ делают настройкой абзаца, а не пробелами или табуляцией.",
                    "Главная → Абзац → Первая строка: Отступ.", "Абзацный отступ", indentBad);
            else Pass("Абзацный отступ");

            if (t.Align == "justify")
            {
                var alignBad = _body.Where(p => ctx.Justification(p.P) == "left" && p.Text.Length > 90).ToList();
                if (alignBad.Count > 0)
                    Add("align", "Основной текст", IssueSeverity.Warning, "Текст не выровнен по ширине",
                        $"Выровнено по левому краю: {Plural(alignBad.Count, "абзац", "абзаца", "абзацев")}.", "Главная → По ширине (Ctrl+J).", "Выравнивание", alignBad);
                else Pass("Выравнивание по ширине");
            }

            if (colorBad.Count > 0)
                Add("color", "Основной текст", IssueSeverity.Warning, "Цветной текст", "Текст работы печатают чёрным.",
                    "Выделите текст → Цвет шрифта → Авто.", "Цвет текста", colorBad);

            // Пустые строки вместо интервалов и разрывов
            var emptyRuns = new List<Para>();
            for (var i = 0; i < paras.Count; i++)
            {
                if (paras[i].Kind != Kind.Empty || paras[i].InTable) continue;
                var j = i;
                while (j + 1 < paras.Count && paras[j + 1].Kind == Kind.Empty && !paras[j + 1].InTable) j++;
                if (j - i + 1 >= 2) emptyRuns.Add(j + 1 < paras.Count ? paras[j + 1] : paras[i]);
                i = j;
            }
            if (emptyRuns.Count > 0)
                Add("emptyLines", "Основной текст", IssueSeverity.Advice, "Пустые строки вместо отступов",
                    $"{Plural(emptyRuns.Count, "место", "места", "мест")}, где подряд несколько пустых строк. При правке текста всё «поедет».",
                    "Новую страницу начинайте разрывом (Ctrl+Enter), расстояние до заголовка задавайте интервалом «перед/после» в абзаце.", null, emptyRuns);

            var doubleSpaces = _body.Where(p => p.Text.Contains("  ")).ToList();
            if (doubleSpaces.Count > 0)
                Add("doubleSpaces", "Основной текст", IssueSeverity.Advice, "Двойные пробелы",
                    $"В {Plural(doubleSpaces.Count, "абзаце", "абзацах", "абзацах")} между словами по два пробела и больше.",
                    "Ctrl+H: найти два пробела, заменить на один — «Заменить все», повторить до нуля замен.", null, doubleSpaces);

            var hyphens = _body.Where(p => p.Text.Contains(" - ")).ToList();
            if (hyphens.Count > 0)
                Add("dash", "Основной текст", IssueSeverity.Advice, "Дефис вместо тире",
                    $"В {Plural(hyphens.Count, "абзаце", "абзацах", "абзацах")} между словами стоит дефис «-». Между словами ставят тире «–» или «—».",
                    "Ctrl+H: « - » заменить на « – » (тире: Ctrl + минус на цифровой клавиатуре).", null, hyphens);
        }

        // ---------------- заголовки

        public void Headings()
        {
            var hd = rules.Headings;
            var structural = paras.Where(p => p.Kind == Kind.Structural).ToList();
            var headings = paras.Where(p => p.Kind == Kind.Heading).ToList();

            bool IsUpper(Para p) => p.Text == p.Text.ToUpperInvariant() || ctx.IsCaps(p.P);
            var caseBad = structural.Where(p => !AppendixTitle().IsMatch(p.Text.Trim()) && (hd.Structural.Case == "upper" ? !IsUpper(p) : IsUpper(p) && p.Text.Trim().Length > 3)).ToList();
            if (caseBad.Count > 0)
                Add("structuralCase", "Заголовки", IssueSeverity.Warning,
                    hd.Structural.Case == "upper" ? "Структурные заголовки не прописными" : "Структурные заголовки прописными",
                    hd.Structural.Case == "upper" ? "«ВВЕДЕНИЕ», «ЗАКЛЮЧЕНИЕ», «СПИСОК ИСПОЛЬЗОВАННЫХ ИСТОЧНИКОВ» пишут прописными буквами."
                        : "По правилам этого вида работы «Введение», «Заключение» пишут с прописной буквы, а не целиком прописными.",
                    "Измените регистр: Главная → Аа (Shift+F3), или включите «Все прописные» в стиле заголовка.", "Структурные элементы", caseBad);
            else if (structural.Count > 0) Pass("Регистр структурных заголовков");

            var alignBad = structural.Where(p => hd.Structural.Align == "center" ? ctx.Justification(p.P) != "center" : ctx.Justification(p.P) == "center").ToList();
            if (alignBad.Count > 0)
                Add("structuralAlign", "Заголовки", IssueSeverity.Warning,
                    hd.Structural.Align == "center" ? "Структурные заголовки не по центру" : "Структурные заголовки по центру",
                    hd.Structural.Align == "center" ? "«Введение», «Заключение» и другие структурные заголовки располагают в середине строки." : "Структурные заголовки пишут с абзацного отступа.",
                    "Главная → По центру (Ctrl+E), без абзацного отступа.", "Структурные элементы", alignBad);

            var levelAlignBad = headings.Where(p => (ctx.OutlineLevel(p.P) ?? 0) <= 2).Where(p =>
            {
                var style = hd.Levels[Math.Clamp(ctx.OutlineLevel(p.P) ?? 0, 0, 2)];
                return style.Align == "center" ? ctx.Justification(p.P) != "center" : ctx.Justification(p.P) == "center";
            }).ToList();
            if (levelAlignBad.Count > 0)
                Add("headingAlign", "Заголовки", IssueSeverity.Warning,
                    hd.Level1.Align == "center" ? "Заголовки глав не по центру" : "Заголовки глав по центру",
                    hd.Level1.Align == "center" ? "Заголовки глав и параграфов располагают посередине строки." : "Заголовки разделов и подразделов пишут с абзацного отступа, а не по центру.",
                    "Измените выравнивание в стиле «Заголовок 1/2»: правой кнопкой по стилю → Изменить.", "Заголовки", levelAlignBad);
            else if (headings.Count > 0) Pass("Положение заголовков");

            var dotBad = structural.Concat(headings).Where(p => p.Text.TrimEnd().EndsWith('.') && !p.Text.TrimEnd().EndsWith("..")).OrderBy(p => p.Index).ToList();
            if (dotBad.Count > 0)
                Add("headingDot", "Заголовки", IssueSeverity.Warning, "Точка в конце заголовка", "В конце заголовков точку не ставят.",
                    "Удалите точку в конце заголовка.", "Заголовки", dotBad);
            else if (structural.Count + headings.Count > 0) Pass("Заголовки без точки в конце");

            // С новой страницы
            var needBreak = new List<Para>();
            if (hd.Structural.NewPage) needBreak.AddRange(structural.Where(p => !IsFirstAfterTitle(p)));
            if (hd.Level1.NewPage) needBreak.AddRange(headings.Where(p => ctx.OutlineLevel(p.P) is 0 || ctx.OutlineLevel(p.P) is null && Regex.IsMatch(p.Text, @"^\s*(глава\s+)?\d+\.?\s", RegexOptions.IgnoreCase)));
            var noBreak = needBreak.Where(p => !StartsNewPage(p)).OrderBy(p => p.Index).ToList();
            if (noBreak.Count > 0)
                Add("newPage", "Заголовки", IssueSeverity.Warning, "Глава или раздел не с новой страницы",
                    "Каждый структурный элемент и каждую главу начинают с новой страницы.",
                    "Поставьте курсор перед заголовком → Ctrl+Enter, или в стиле заголовка: Абзац → Положение на странице → «с новой страницы».",
                    "Новая страница", noBreak);
            else if (needBreak.Count > 0) Pass("Главы с новой страницы");

            var unstyled = paras.Where(p => (p.Kind is Kind.Heading or Kind.Structural) && ctx.OutlineLevel(p.P) is null && !AppendixTitle().IsMatch(p.Text.Trim())
                                              && !Regex.IsMatch(p.Text.Trim(), @"^(содержание|оглавление)$", RegexOptions.IgnoreCase)).ToList();
            if (unstyled.Count > 0)
                Add("headingStyle", "Заголовки", IssueSeverity.Advice, "Заголовок оформлен вручную",
                    $"Набрано обычным текстом, без стиля заголовка: {Plural(unstyled.Count, "заголовок", "заголовка", "заголовков")}. Такие заголовки не попадут в автоматическое содержание.",
                    "Выделите заголовок → стиль «Заголовок 1» (главы) или «Заголовок 2» (параграфы).", null, unstyled);
        }

        bool IsFirstAfterTitle(Para p) => !paras.Any(x => x.Index < p.Index && x.Kind is Kind.Body or Kind.Heading or Kind.Structural or Kind.Toc);

        bool StartsNewPage(Para p)
        {
            if (ctx.PageBreakBefore(p.P)) return true;
            if (p.P.Elements<Run>().FirstOrDefault()?.Elements<Break>().Any(b => b.Type?.Value == BreakValues.Page) == true) return true;
            // Предыдущие абзацы (пустые допускаются): разрыв страницы или раздела
            for (var e = p.P.PreviousSibling(); e != null; e = e.PreviousSibling())
            {
                if (e is not Paragraph prev) return false;
                if (prev.ParagraphProperties?.SectionProperties != null) return true;
                if (prev.Descendants<Break>().Any(b => b.Type?.Value == BreakValues.Page)) return true;
                if (!string.IsNullOrWhiteSpace(prev.InnerText)) return false;
            }
            return true; // начало документа
        }

        // ---------------- содержание

        public void Toc(Body body)
        {
            var hasField = body.Descendants<FieldCode>().Any(f => f.Text.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase))
                           || body.Descendants<SimpleField>().Any(f => f.Instruction?.Value?.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase) == true)
                           || body.Descendants<SdtBlock>().Any(s => s.SdtProperties?.GetFirstChild<SdtContentDocPartObject>()?.GetFirstChild<DocPartGallery>()?.Val?.Value == "Table of Contents");
            var tocHeading = paras.FirstOrDefault(p => Regex.IsMatch(p.Text.Trim(), @"^(содержание|оглавление)$", RegexOptions.IgnoreCase));
            var chapters = paras.Count(p => p.Kind == Kind.Heading);
            if (hasField) { Pass("Автособираемое содержание"); return; }
            if (tocHeading != null)
                Add("toc", "Содержание", IssueSeverity.Warning, "Содержание набрано вручную",
                    "Номера страниц в нём не обновятся при правке текста и часто расходятся с работой.",
                    "Ссылки → Оглавление → Автособираемое оглавление (заголовки должны быть оформлены стилями).", "Содержание", [tocHeading]);
            else if (chapters >= 2 && rules.Structure.Any(i => i.Type == "toc"))
                Add("toc", "Содержание", IssueSeverity.Warning, "Нет содержания", "В работе с главами после титульного листа идёт содержание.",
                    "Ссылки → Оглавление → Автособираемое оглавление.", "Содержание", One(FirstContent));
        }

        // ---------------- рисунки и таблицы

        public void Captions(Body body)
        {
            var cr = rules.Captions;
            var figures = paras.Where(p => p.Kind == Kind.Caption && FigureCaption().IsMatch(p.Text.Trim())).Select(p => (p, m: FigureCaption().Match(p.Text.Trim()))).ToList();
            var tables = paras.Where(p => p.Kind == Kind.Caption && TableCaption().IsMatch(p.Text.Trim())).Select(p => (p, m: TableCaption().Match(p.Text.Trim()))).ToList();
            var sepExpected = cr.Separator.Trim() == "." ? "." : "–";
            static string SepOf(string s) => s == "." ? "." : s.Length > 0 ? "–" : "";

            if (figures.Count > 0)
            {
                var prefixBad = figures.Where(f => cr.Figure.Prefix.StartsWith("Рисунок") ? f.m.Groups[1].Value.StartsWith("Рис.", StringComparison.OrdinalIgnoreCase) : !f.m.Groups[1].Value.StartsWith("Рис.", StringComparison.OrdinalIgnoreCase)).Select(f => f.p).ToList();
                if (prefixBad.Count > 0)
                    Add("figurePrefix", "Рисунки и таблицы", IssueSeverity.Warning, $"Подпись рисунка не «{cr.Figure.Prefix} N»",
                        $"Слово «{cr.Figure.Prefix}» пишут полностью (или как требует методичка), без сокращения.", "Исправьте подпись: «Рисунок 1 – Название».", "Подписи рисунков", prefixBad);
                var sepBad = figures.Where(f => SepOf(f.m.Groups[4].Value) != sepExpected).Select(f => f.p).ToList();
                if (sepBad.Count > 0)
                    Add("figureSeparator", "Рисунки и таблицы", IssueSeverity.Warning, "Знак в подписи рисунка",
                        sepExpected == "–" ? "Номер и название рисунка разделяют тире: «Рисунок 1 – Название»." : "Номер и название рисунка разделяют точкой: «Рисунок 1. Название».",
                        null, "Подписи рисунков", sepBad);
                var dotBad = figures.Where(f => f.p.Text.TrimEnd().EndsWith('.')).Select(f => f.p).ToList();
                if (dotBad.Count > 0)
                    Add("captionDot", "Рисунки и таблицы", IssueSeverity.Warning, "Точка в конце подписи рисунка", "В конце названия рисунка точку не ставят.", null, "Подписи рисунков", dotBad);
                var alignBad = figures.Where(f => (cr.Figure.Align == "center") != (ctx.Justification(f.p.P) == "center")).Select(f => f.p).ToList();
                if (alignBad.Count > 0)
                    Add("figureAlign", "Рисунки и таблицы", IssueSeverity.Warning, cr.Figure.Align == "center" ? "Подпись рисунка не по центру" : "Подпись рисунка по центру",
                        cr.Figure.Align == "center" ? "Подпись располагают по центру под рисунком." : "Подпись располагают слева.", null, "Подписи рисунков", alignBad);
                Numbering(figures.Select(f => (f.p, f.m.Groups[3].Value)).ToList(), "figureNumbering", "рисунков", "Рисунок");
                Referenced(figures.Select(f => (f.p, f.m.Groups[3].Value)).ToList(), FigureReference(), "figureRef", "рисунок", "рисунке");
                if (prefixBad.Count + sepBad.Count + dotBad.Count + alignBad.Count == 0) Pass("Подписи рисунков");
            }

            if (tables.Count > 0)
            {
                var alignBad = tables.Where(t => (cr.Table.Align == "center") != (ctx.Justification(t.p.P) == "center")).Select(t => t.p).ToList();
                if (alignBad.Count > 0)
                    Add("tableAlign", "Рисунки и таблицы", IssueSeverity.Warning, cr.Table.Align == "center" ? "Название таблицы не по центру" : "Название таблицы не слева",
                        cr.Table.Align == "center" ? "Название таблицы располагают по центру." : "Название таблицы помещают над таблицей слева, без абзацного отступа.", null, "Подписи таблиц", alignBad);
                var below = tables.Where(t => t.p.P.PreviousSibling() is Table && t.p.P.NextSibling() is not Table).Select(t => t.p).ToList();
                if (below.Count > 0)
                    Add("tableAbove", "Рисунки и таблицы", IssueSeverity.Warning, "Название таблицы под таблицей", "Название таблицы помещают над таблицей.", null, "Подписи таблиц", below);
                var sepBad = tables.Where(t => t.m.Groups[4].Value.Trim().Length > 0 && SepOf(t.m.Groups[3].Value) != sepExpected).Select(t => t.p).ToList();
                if (sepBad.Count > 0)
                    Add("tableSeparator", "Рисунки и таблицы", IssueSeverity.Warning, "Знак в названии таблицы",
                        sepExpected == "–" ? "Номер и название таблицы разделяют тире: «Таблица 1 – Название»." : "Номер и название таблицы разделяют точкой.", null, "Подписи таблиц", sepBad);
                Numbering(tables.Select(t => (t.p, t.m.Groups[2].Value)).ToList(), "tableNumbering", "таблиц", "Таблица");
                Referenced(tables.Select(t => (t.p, t.m.Groups[2].Value)).ToList(), TableReference(), "tableRef", "таблицу", "таблице");
                if (alignBad.Count + below.Count + sepBad.Count == 0) Pass("Названия таблиц");
            }

            // Рисунки без подписи: картинка, а за ней в трёх абзацах нет «Рисунок N»
            var noCaption = new List<Para>();
            for (var i = 0; i < paras.Count; i++)
            {
                var p = paras[i];
                if (p.Kind == Kind.Title || p.InTable || !p.P.Descendants<Drawing>().Any()) continue;
                var near = paras.Skip(i).Take(4).Any(x => FigureCaption().IsMatch(x.Text.Trim()));
                if (!near) noCaption.Add(p);
            }
            if (noCaption.Count > 0)
                Add("figureCaption", "Рисунки и таблицы", IssueSeverity.Warning, "Рисунок без подписи",
                    $"{Plural(noCaption.Count, "рисунок", "рисунка", "рисунков")} без подписи «Рисунок N – Название» под ним.",
                    "Ссылки → Вставить название → «Рисунок» — номер будет обновляться сам.", "Подписи рисунков", noCaption);
        }

        void Numbering(List<(Para P, string Number)> items, string key, string what, string word)
        {
            // Сквозная (1, 2, 3) или по главам (1.1, 1.2, 2.1) — проверяем повторы и пропуски внутри своей группы
            var bad = new List<Para>();
            var seen = new HashSet<string>();
            var lastInGroup = new Dictionary<string, int>();
            foreach (var (p, number) in items)
            {
                if (!seen.Add(number)) { bad.Add(p); continue; }
                var parts = number.Split('.');
                var group = parts.Length > 1 ? string.Join('.', parts[..^1]) : "";
                if (!int.TryParse(parts[^1], out var n)) continue;
                var expected = lastInGroup.GetValueOrDefault(group) + 1;
                if (n != expected) bad.Add(p);
                lastInGroup[group] = n;
            }
            if (bad.Count > 0)
                Add(key, "Рисунки и таблицы", IssueSeverity.Warning, $"Нумерация {what} сбита",
                    $"Номера повторяются или идут с пропуском: {string.Join(", ", bad.Take(5).Select(b => Short(b.Text, 25)))}.",
                    $"Используйте автоматическую нумерацию: Ссылки → Вставить название → «{word}», затем Ctrl+A и F9.", null, bad);
        }

        void Referenced(List<(Para P, string Number)> items, Regex reference, string key, string accusative, string prepositional)
        {
            var text = string.Join("\n", paras.Where(p => p.Kind is Kind.Body or Kind.Other or Kind.Reference && !p.InTable).Select(p => p.Text));
            var refs = reference.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
            var missing = items.Where(i => !refs.Contains(i.Number)).Select(i => i.P).ToList();
            if (missing.Count > 0)
                Add(key, "Рисунки и таблицы", IssueSeverity.Warning, $"Нет ссылки на {accusative} в тексте",
                    $"На каждый {(accusative == "рисунок" ? "рисунок" : "таблицу")} в тексте дают ссылку, например: «…приведено на {prepositional} 1». Без ссылки: {Plural(missing.Count, "подпись", "подписи", "подписей")}.",
                    $"Добавьте в текст перед {(accusative == "рисунок" ? "рисунком" : "таблицей")} фразу со ссылкой на номер.", null, missing);
        }

        // ---------------- список литературы (ГОСТ Р 7.0.100-2018, 7.0.5-2008, 7.0.108-2022)

        public void References()
        {
            var heading = paras.FirstOrDefault(p => p.Kind == Kind.Structural && ReferencesTitle().IsMatch(p.Text.Trim()));
            var items = paras.Where(p => p.Kind == Kind.Reference && p.Text.Trim().Length > 10).ToList();
            if (heading == null)
            {
                if (_body.Count >= 15)
                    Add("references", "Список литературы", IssueSeverity.Warning, "Нет списка использованных источников",
                        "В конце работы перед приложениями приводят список использованных источников.", null, "ГОСТ 7.32 / методичка", One(FirstContent));
                return;
            }
            if (items.Count == 0)
            {
                Add("references", "Список литературы", IssueSeverity.Warning, "Список источников пуст", "После заголовка списка нет ни одного источника.", null, null, [heading]);
                return;
            }

            var notNumbered = items.Where(p => !ctx.IsList(p.P) && !Regex.IsMatch(p.Text.TrimStart(), @"^\d+[.)]?\s")).ToList();
            if (notNumbered.Count > 0)
                Add("refNumbering", "Список литературы", IssueSeverity.Advice, "Источники без номеров",
                    "Источники в списке нумеруют арабскими цифрами — по ним даются ссылки [1], [2, с. 15].",
                    "Выделите список → Главная → Нумерация.", "ГОСТ Р 7.0.5", notNumbered);

            var urls = items.Where(p => Regex.IsMatch(p.Text, @"https?://|www\.", RegexOptions.IgnoreCase)).ToList();
            var noUrlLabel = urls.Where(p => !p.Text.Contains("URL:", StringComparison.OrdinalIgnoreCase)).ToList();
            var noDate = urls.Where(p => !Regex.IsMatch(p.Text, @"дата\s+обращени", RegexOptions.IgnoreCase)).ToList();
            if (noDate.Count > 0)
                Add("refDate", "Список литературы", IssueSeverity.Warning, "У электронного источника нет даты обращения",
                    "После адреса указывают дату обращения: «URL: https://… (дата обращения: 15.10.2026)».", null, "ГОСТ Р 7.0.100-2018, Р 7.0.108-2022", noDate);
            if (noUrlLabel.Count > 0)
                Add("refUrl", "Список литературы", IssueSeverity.Advice, "Адрес сайта без «URL:»",
                    "Электронный адрес в описании приводят после слова «URL:».", null, "ГОСТ Р 7.0.100-2018", noUrlLabel);

            // Ссылки в тексте [N]
            var cited = new HashSet<int>();
            var badRefs = new List<Para>();
            foreach (var p in paras.Where(p => p.Kind is Kind.Body or Kind.Other or Kind.Caption && p.Index < heading.Index))
                foreach (Match m in Citation().Matches(p.Text))
                    foreach (var part in m.Groups[1].Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var range = part.Split(['–', '-']);
                        if (int.TryParse(range[0].Trim(), out var a))
                        {
                            var b = range.Length > 1 && int.TryParse(range[1].Trim(), out var bb) ? bb : a;
                            for (var n = a; n <= Math.Min(b, a + 50); n++) { cited.Add(n); if (n > items.Count) badRefs.Add(p); }
                        }
                    }
            if (cited.Count == 0)
                Add("citations", "Список литературы", IssueSeverity.Warning, "В тексте нет ссылок на источники",
                    "На каждый источник из списка в тексте дают ссылку в квадратных скобках: [1], [2, с. 15].", null, "ГОСТ Р 7.0.5-2008", [heading]);
            else
            {
                if (badRefs.Count > 0)
                    Add("citationRange", "Список литературы", IssueSeverity.Warning, "Ссылка на несуществующий источник",
                        $"В тексте есть ссылки на номера больше {items.Count} — столько источников в списке.", null, "ГОСТ Р 7.0.5-2008", badRefs.Distinct());
                var uncited = items.Select((p, i) => (p, n: i + 1)).Where(x => !cited.Contains(x.n)).Select(x => x.p).ToList();
                if (uncited.Count > 0)
                    Add("uncited", "Список литературы", IssueSeverity.Advice, "Источник без ссылки в тексте",
                        $"На {Plural(uncited.Count, "источник", "источника", "источников")} из списка в тексте нет ссылки [N].", null, "ГОСТ Р 7.0.5-2008", uncited);
                if (badRefs.Count == 0 && uncited.Count == 0) Pass("Ссылки на источники");
            }
            if (noDate.Count + noUrlLabel.Count == 0 && items.Count > 0) Pass("Описание электронных источников");
        }

        // ---------------- приложения

        public void Appendices()
        {
            var apps = paras.Where(p => p.Kind == Kind.Structural && AppendixTitle().IsMatch(p.Text.Trim()))
                .Select(p => (p, letter: char.ToUpperInvariant(AppendixTitle().Match(p.Text.Trim()).Groups[1].Value[0]))).ToList();
            if (apps.Count == 0) return;
            var forbidden = apps.Where(a => "ЁЗЙОЧЪЫЬ".Contains(a.letter)).Select(a => a.p).ToList();
            if (forbidden.Count > 0)
                Add("appendixLetter", "Приложения", IssueSeverity.Warning, "Недопустимая буква приложения",
                    "Приложения обозначают буквами А, Б, В, Г, Д, Е, Ж, И, К… — без Ё, З, Й, О, Ч, Ъ, Ы, Ь.", null, "ГОСТ 7.32, Р 2.105", forbidden);
            const string order = "АБВГДЕЖИКЛМНПРСТУФХЦШЩЭЮЯ";
            var outOfOrder = apps.Where((a, i) => order.IndexOf(a.letter) != i && a.letter is >= 'А' and <= 'Я').Select(a => a.p).ToList();
            if (outOfOrder.Count > 0 && forbidden.Count == 0)
                Add("appendixOrder", "Приложения", IssueSeverity.Advice, "Приложения не по порядку", "Приложения обозначают буквами по порядку: А, Б, В…", null, null, outOfOrder);
            if (forbidden.Count + outOfOrder.Count == 0) Pass("Обозначения приложений");
        }

        static string Plural(int n, string one, string few, string many) => CheckReport.Plural(n, one, few, many);
    }
}
