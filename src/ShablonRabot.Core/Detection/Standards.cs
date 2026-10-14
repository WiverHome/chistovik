namespace ShablonRabot.Core.Detection;

/// <summary>Правило эталона: подходит ли найденное значение и как назвать требование ГОСТ.</summary>
public sealed record StandardRule(string Key, Func<object, bool> Matches, string Requirement);

public sealed record Standard(string Id, string Name, string Scope, IReadOnlyList<StandardRule> Rules);

public sealed record RuleDifference(string Key, string Name, string Found, string Required);

public enum VerdictKind { Matches, CustomRules, NotEnoughData }

/// <summary>Итог: на какой ГОСТ похожи найденные правила.</summary>
public sealed record StandardVerdict(VerdictKind Kind, Standard? Standard, int Matched, int Compared, IReadOnlyList<RuleDifference> Differences)
{
    /// <summary>Другие эталоны, которым найденные правила соответствуют полностью: у ГОСТов много общих требований.</summary>
    public IReadOnlyList<Standard> AlsoMatches { get; init; } = [];

    public string Title => Kind switch
    {
        VerdictKind.Matches => $"Похоже на {Standard!.Name}",
        VerdictKind.CustomRules => "Своя методичка",
        _ => "Мало данных, чтобы определить ГОСТ",
    };

    public string Summary(int found) => Kind switch
    {
        VerdictKind.Matches => $"Совпадает {Matched} из {Compared} найденных правил. {Standard!.Scope}"
            + (AlsoMatches.Count > 0 ? $" Эти правила подходят и под {string.Join(", ", AlsoMatches.Select(s => s.Name))}." : ""),
        VerdictKind.CustomRules => $"Правила заметно отличаются от ГОСТ: с ближайшим ({Standard!.Name}) совпадает {Matched} из {Compared}. Это нормально — многие вузы пишут свои требования.",
        _ => $"Найдено правил: {found}. Для вывода о ГОСТ нужно хотя бы 6. Найденные правила можно применить.",
    };
}

public static class Standards
{
    static bool Near(object v, double x) => v is double d && Math.Abs(d - x) < 0.051;

    static Func<object, bool> Margins(double l, double r, double t, double b) => v =>
        v is MarginsValue m
        && (m.Left is not { } ml || Math.Abs(ml - l) < 1.1) && (m.Right is not { } mr || Math.Abs(mr - r) < 1.1)
        && (m.Top is not { } mt || Math.Abs(mt - t) < 1.1) && (m.Bottom is not { } mb || Math.Abs(mb - b) < 1.1);

    /// <summary>ГОСТ 7.32-2017 «Отчёт о НИР», 6.1–6.6 — на нём основано большинство требований к курсовым и ВКР.</summary>
    public static readonly Standard Gost732 = new("gost-7.32-2017", "ГОСТ 7.32-2017",
        "Этот стандарт обычно используют для курсовых и выпускных работ.",
    [
        new("margins", Margins(30, 15, 20, 20), "30 / 15 / 20 / 20 мм"),
        new("fontSize", v => v is double d && d >= 12, "не меньше 12 пт"),
        new("lineSpacing", v => Near(v, 1.5), "1,5"),
        new("indent", v => Near(v, 1.25), "1,25 см"),
        new("align", v => Equals(v, "justify"), "по ширине"),
        new("pageNumber", v => Equals(v, "center"), "внизу по центру"),
        new("headingAlign", v => Equals(v, "indent"), "с абзацного отступа"),
        new("structuralUpper", v => Equals(v, true), "прописными"),
        new("chapterNewPage", v => Equals(v, true), "с новой страницы"),
        new("figurePrefix", v => Equals(v, "Рисунок"), "«Рисунок»"),
        new("separator", v => Equals(v, " – "), "тире"),
        new("tableCaptionAlign", v => Equals(v, "left"), "слева"),
    ]);

    /// <summary>ГОСТ Р 7.0.11-2011 «Диссертация и автореферат диссертации», 5.3.4–5.3.8.</summary>
    public static readonly Standard Gost7011 = new("gost-r-7.0.11-2011", "ГОСТ Р 7.0.11-2011",
        "Этот стандарт задаёт оформление диссертаций; некоторые вузы применяют его и к ВКР.",
    [
        new("margins", Margins(25, 10, 20, 20), "25 / 10 / 20 / 20 мм"),
        new("fontSize", v => v is double d && d is >= 12 and <= 14, "12–14 пт"),
        new("lineSpacing", v => Near(v, 1.5), "1,5"),
        // «Пять знаков» — для 12–14 пт это примерно 1–1,5 см
        new("indent", v => v is double d && d is >= 0.95 and <= 1.55, "пять знаков (около 1,25 см)"),
        new("pageNumber", v => Equals(v, "top-center"), "вверху по центру"),
        new("headingAlign", v => Equals(v, "center"), "по центру"),
        new("chapterNewPage", v => Equals(v, true), "с новой страницы"),
    ]);

    /// <summary>
    /// ГОСТ Р 2.105-2019 «ЕСКД. Общие требования к текстовым документам» с Изменением № 1 (с 01.02.2021).
    /// Поля задаются от рамки, положение номера страницы — основной надписью, поэтому эти правила не сравниваются.
    /// </summary>
    public static readonly Standard Gost2105 = new("gost-r-2.105-2019", "ГОСТ Р 2.105-2019",
        "Этот стандарт (ЕСКД) часто требуют в технических вузах для пояснительных записок к курсовым и дипломным проектам.",
    [
        new("fontSize", v => v is double d && d is >= 11 and <= 14, "11–14 пт"),
        new("lineSpacing", v => v is double d && new[] { 1, 1.15, 1.25, 1.5, 1.75, 2 }.Any(x => Near(d, x)), "1,5 (допускается 1; 1,15; 1,25; 1,75; 2)"),
        new("indent", v => v is double d && d is >= 1.2 and <= 1.75, "12,5–17 мм"),
        new("headingAlign", v => Equals(v, "indent"), "с абзацного отступа"),
        new("structuralUpper", v => Equals(v, false), "с прописной буквы, не прописными"),
        new("figurePrefix", v => Equals(v, "Рисунок"), "«Рисунок»"),
        new("separator", v => Equals(v, " – "), "тире"),
        new("tableCaptionAlign", v => Equals(v, "left"), "слева"),
    ]);

    public static readonly IReadOnlyList<Standard> All = [Gost732, Gost7011, Gost2105];

    /// <summary>Сравнивает найденные правила со всеми эталонами и выбирает ближайший.</summary>
    public static StandardVerdict Match(RulesDetection detection)
    {
        var usable = detection.Rules.Where(r => r.Confidence >= Confidence.Medium).ToList();
        var candidates = new List<(Standard Standard, int Matched, int Compared, List<RuleDifference> Diffs, double Score)>();
        foreach (var standard in All)
        {
            var compared = 0;
            var matched = 0;
            var diffs = new List<RuleDifference>();
            foreach (var rule in standard.Rules)
            {
                var found = usable.FirstOrDefault(r => r.Key == rule.Key);
                if (found == null) continue;
                compared++;
                if (rule.Matches(found.Value)) matched++;
                else diffs.Add(new RuleDifference(rule.Key, found.Name, found.Display, rule.Requirement));
            }
            candidates.Add((standard, matched, compared, diffs, compared == 0 ? 0 : (double)matched / compared));
        }
        // Ближайший — по доле совпадений; при равной доле — тот, с которым совпало больше правил;
        // при полном равенстве остаётся первый эталон (ГОСТ 7.32-2017)
        var best = candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.Matched).FirstOrDefault();
        if (best.Standard == null) return new StandardVerdict(VerdictKind.NotEnoughData, null, 0, 0, []);
        // DESIGN.md 17.4.4: вывод о ГОСТ — от 6 найденных правил, «похоже» — от 80 % совпадений
        var kind = usable.Count < 6 || best.Compared < 3 ? VerdictKind.NotEnoughData
            : best.Score >= 0.8 ? VerdictKind.Matches
            : VerdictKind.CustomRules;
        var also = kind == VerdictKind.Matches
            ? candidates.Where(c => c.Standard != best.Standard && c.Compared >= 3 && c.Matched == c.Compared).Select(c => c.Standard).ToList()
            : [];
        return new StandardVerdict(kind, best.Standard, best.Matched, best.Compared, best.Diffs) { AlsoMatches = also };
    }
}
