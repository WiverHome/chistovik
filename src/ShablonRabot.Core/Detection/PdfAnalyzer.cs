using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ShablonRabot.Core.Detection;

/// <summary>
/// Определяет правила по PDF без Word: по координатам, шрифту и кеглю букв.
/// Результат приблизительный — в PDF нет стилей, только отрисованные строки.
/// </summary>
public static partial class PdfAnalyzer
{
    const double MmPerPt = 25.4 / 72, CmPerPt = 2.54 / 72;

    [GeneratedRegex(@"^\s*(Рисунок|Рис\.)\s*[А-ЯA-Z]?\.?\s*\d+(?:[.\-]\d+)*\s*([–—\-]|\.)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex FigureCaption();

    [GeneratedRegex(@"^\s*Таблица\s+[А-ЯA-Z]?\.?\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex TableCaption();

    [GeneratedRegex(@"^(введение|заключение|содержание|оглавление|список (использованных|литературы|источников))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StructuralTitle();

    [GeneratedRegex(@"^\s*\d{1,3}\s*$")]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"^[A-Z]{6}\+")]
    private static partial Regex SubsetPrefix();

    sealed record Line(int Page, double Left, double Right, double Baseline, double Top, double Size, string? Font, string Text, double PageWidth, double PageHeight)
    {
        public double Center => (Left + Right) / 2;
    }

    public static RulesDetection Analyze(string path, string? displayName = null)
    {
        var result = new RulesDetection { Source = displayName ?? Path.GetFileName(path), Approximate = true };
        using var pdf = PdfDocument.Open(path);
        var lines = new List<Line>();
        foreach (var page in pdf.GetPages())
            lines.AddRange(LinesOf(page));
        if (lines.Count == 0) return result;

        // Основной текст: длинные строки преобладающего кегля
        var bodySize = Mode(lines.Where(l => l.Text.Length >= 40).Select(l => Math.Round(l.Size * 2) / 2)) ?? Mode(lines.Select(l => Math.Round(l.Size * 2) / 2));
        var body = lines.Where(l => l.Text.Length >= 40 && Math.Abs(l.Size - (bodySize ?? 0)) < 0.6).ToList();
        if (body.Count < 5) return result;
        var pages = body.Select(l => l.Page).Distinct().Count();
        var conf = pages >= 3 ? Confidence.Medium : Confidence.Low;

        // Поля. Левое — самый левый из частых краёв строк (строки с абзацным отступом начинаются правее);
        // правое — самый частый правый край; верхнее — по самой высокой строке; нижнее — только по заполненным страницам
        var lefts = body.Select(l => Math.Round(l.Left * MmPerPt)).GroupBy(x => x).Where(g => g.Count() >= Math.Max(3, body.Count * 0.15)).Select(g => g.Key).ToList();
        var left = lefts.Count > 0 ? lefts.Min() : Mode(body.Select(l => Math.Round(l.Left * MmPerPt)))!.Value;
        var right = Mode(body.Select(l => Math.Round((l.PageWidth - l.Right) * MmPerPt)))!.Value;
        var content = lines.Where(l => !PageNumber().IsMatch(l.Text)).ToList();
        double? topMargin = content.GroupBy(l => l.Page).Select(g => Math.Round((g.First().PageHeight - g.Max(l => l.Top)) * MmPerPt)).Min();
        // Нижнее поле — по основному тексту (строки колонтитулов, например «Город, год» на титуле, не учитываются)
        var fullPages = body.GroupBy(l => l.Page).Where(g => g.Min(l => l.Baseline) < g.First().PageHeight * 0.2).ToList();
        double? bottom = fullPages.Count > 0 ? fullPages.Select(g => Math.Round(g.Min(l => l.Baseline - l.Size * 0.25) * MmPerPt)).Min() : null;
        var margins = new MarginsValue(left, right, topMargin, bottom);
        result.Add("margins", margins, margins.IsComplete || bottom == null ? conf : Confidence.Low,
            $"По положению текста на {Rules.Plural(pages, "странице", "страницах", "страницах")}");

        result.Add("fontSize", bodySize!.Value, Confidence.Medium, "Кегль основного текста в PDF");
        var font = Mode(body.Select(l => l.Font).OfType<string>());
        if (font != null) result.Add("font", font, Confidence.Medium, "Шрифт, встроенный в PDF");

        // Интервал: расстояние между базовыми линиями соседних строк основного текста
        var gaps = new List<double>();
        foreach (var g in body.GroupBy(l => l.Page))
        {
            var ordered = g.OrderByDescending(l => l.Baseline).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var d = ordered[i - 1].Baseline - ordered[i].Baseline;
                if (d > 0 && d < bodySize.Value * 3) gaps.Add(d);
            }
        }
        if (Mode(gaps.Select(d => Math.Round(d * 2) / 2)) is { } gap)
        {
            var multiple = gap / (bodySize.Value * 1.15);
            double[] known = [1.0, 1.15, 1.5, 2.0];
            var nearest = known.OrderBy(k => Math.Abs(k - multiple)).First();
            result.Add("lineSpacing", nearest, Math.Abs(nearest - multiple) < 0.08 ? Confidence.Medium : Confidence.Low,
                "По расстоянию между строками");
        }

        // Абзацный отступ: строки, начинающиеся правее левого края на 0,5–3 см
        var leftPt = left / MmPerPt;
        var indents = body.Select(l => (l.Left - leftPt) * CmPerPt).Where(d => d is > 0.4 and < 3).Select(d => Math.Round(d * 20) / 20).ToList();
        // Отступ по глифам чуть больше настоящего — привязываем к ближайшему типичному значению
        double[] typical = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
        indents = indents.Select(d => typical.OrderBy(t => Math.Abs(t - d)).First() is var t && Math.Abs(t - d) <= 0.1 ? t : d).ToList();
        if (Mode(indents) is { } indent && indents.Count >= 3)
            result.Add("indent", indent, Confidence.Medium, "По началу первых строк абзацев");

        // Выравнивание: доля строк, доходящих до правого края
        var rightPt = right / MmPerPt;
        var flush = body.Count(l => Math.Abs(l.PageWidth - l.Right - rightPt) < 3);
        var justify = (double)flush / body.Count > 0.6;
        result.Add("align", justify ? "justify" : "left", Confidence.Medium, justify ? "Строки доходят до правого края" : "Правый край неровный");

        // Переносы: строки, заканчивающиеся дефисом
        var hyphenated = body.Count(l => l.Text.EndsWith('-') || l.Text.EndsWith('­'));
        result.Add("hyphenation", hyphenated >= 3, Confidence.Low, hyphenated >= 3 ? "Есть переносы слов" : "Переносов слов не видно");

        // Номер страницы: короткая строка из цифр у верхнего или нижнего края
        var numbers = lines.Where(l => PageNumber().IsMatch(l.Text) && (l.Baseline < l.PageHeight * 0.12 || l.Baseline > l.PageHeight * 0.9)).ToList();
        if (numbers.Count >= 2)
        {
            var top = numbers.Count(n => n.Baseline > n.PageHeight * 0.5) * 2 > numbers.Count;
            var centered = numbers.Count(n => Math.Abs(n.Center - n.PageWidth / 2) < 40) * 2 > numbers.Count;
            var value = (top ? "top-" : "") + (centered ? "center" : "right");
            result.Add("pageNumber", value, Confidence.Medium, $"Номера на {Rules.Plural(numbers.Count, "странице", "страницах", "страницах")}");
        }

        // Структурные заголовки и подписи
        var structural = lines.Where(l => StructuralTitle().IsMatch(l.Text)).ToList();
        if (structural.Count >= 2)
        {
            var upper = structural.Count(l => l.Text == l.Text.ToUpperInvariant());
            result.Add("structuralUpper", upper * 2 >= structural.Count, Confidence.Medium,
                $"{Rules.Plural(structural.Count, "структурный заголовок", "структурных заголовка", "структурных заголовков")}");
        }
        var figures = lines.Select(l => (l, m: FigureCaption().Match(l.Text))).Where(x => x.m.Success).ToList();
        if (figures.Count > 0)
        {
            var prefix = figures.Count(f => f.m.Groups[1].Value.StartsWith("Рис.", StringComparison.OrdinalIgnoreCase)) * 2 > figures.Count ? "Рис." : "Рисунок";
            result.Add("figurePrefix", prefix, Confidence.Medium, $"{Rules.Plural(figures.Count, "подпись", "подписи", "подписей")} рисунков");
            var seps = figures.Where(f => f.m.Groups[2].Value.Length > 0).Select(f => f.m.Groups[2].Value == "." ? ". " : " – ").ToList();
            if (seps.Count > 0)
                result.Add("separator", Mode(seps)!, Confidence.Medium, $"{Rules.Plural(seps.Count, "подпись", "подписи", "подписей")} рисунков");
        }
        var tables = lines.Where(l => TableCaption().IsMatch(l.Text)).ToList();
        if (tables.Count > 0)
        {
            var centered = tables.Count(t => Math.Abs(t.Center - t.PageWidth / 2 + (left - right) / MmPerPt / 2) < 20 && t.Left - leftPt > 30);
            result.Add("tableCaptionAlign", centered * 2 > tables.Count ? "center" : "left", Confidence.Medium,
                $"{Rules.Plural(tables.Count, "подпись", "подписи", "подписей")} таблиц");
        }

        result.Verdict = Standards.Match(result);
        return result;
    }

    /// <summary>Строки страницы: буквы группируются по базовой линии и сортируются слева направо.</summary>
    static IEnumerable<Line> LinesOf(Page page)
    {
        var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value) || l.Value == " ").ToList();
        foreach (var group in letters.GroupBy(l => Math.Round(l.StartBaseLine.Y / 2) * 2))
        {
            var ordered = group.OrderBy(l => l.StartBaseLine.X).ToList();
            var visible = ordered.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            if (visible.Count == 0) continue;
            var text = string.Concat(ordered.Select(l => l.Value)).Trim();
            var size = visible.GroupBy(l => Math.Round(l.PointSize * 2) / 2).OrderByDescending(g => g.Count()).First().Key;
            var font = visible.GroupBy(l => l.FontName).OrderByDescending(g => g.Count()).First().Key;
            yield return new Line(page.Number, visible.Min(l => l.GlyphRectangle.Left), visible.Max(l => l.GlyphRectangle.Right), group.Key,
                visible.Max(l => l.GlyphRectangle.Top), size, CleanFont(font), text, page.Width, page.Height);
        }
    }

    /// <summary>«ABCDEF+TimesNewRomanPSMT» → «Times New Roman».</summary>
    static string? CleanFont(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var n = SubsetPrefix().Replace(name, "").Split(',', '-')[0].Replace("PSMT", "").Replace("MT", "").Replace("PS", "");
        return n.ToLowerInvariant() switch
        {
            var s when s.StartsWith("timesnewroman") || s.StartsWith("times") => "Times New Roman",
            var s when s.StartsWith("arial") => "Arial",
            var s when s.StartsWith("calibri") => "Calibri",
            var s when s.StartsWith("cambria") => "Cambria",
            var s when s.StartsWith("ptserif") => "PT Serif",
            var s when s.StartsWith("ptastraserif") => "PT Astra Serif",
            var s when s.StartsWith("liberationserif") => "Liberation Serif",
            var s when s.StartsWith("georgia") => "Georgia",
            var s when s.StartsWith("couriernew") => "Courier New",
            _ => Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " "),
        };
    }

    static T? Mode<T>(IEnumerable<T> values) where T : notnull =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    static double? Mode(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : list.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
    }
}
