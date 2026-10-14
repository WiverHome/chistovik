namespace ShablonRabot.Core.Detection;

public enum Confidence { Low, Medium, High }

/// <summary>Поля страницы, найденные частично (в тексте требований могут назвать только часть полей).</summary>
public sealed record MarginsValue(double? Left, double? Right, double? Top, double? Bottom)
{
    public bool IsComplete => Left != null && Right != null && Top != null && Bottom != null;

    public override string ToString()
    {
        static string F(double? v) => v is { } x ? Rules.Num(x) : "—";
        // Л / П / В / Н; ненайденная сторона — «—» (DESIGN.md 17.4.5)
        return $"{F(Left)} / {F(Right)} / {F(Top)} / {F(Bottom)} мм";
    }
}

/// <summary>
/// Найденное правило. Key — ключ правила раздела «Правила оформления» (margins, fontSize, …);
/// Value — значение в формате профиля (MarginsValue, double, string, bool, int).
/// </summary>
public sealed record DetectedRule(string Key, object Value, Confidence Confidence, string Evidence)
{
    public string Name => Rules.Names.GetValueOrDefault(Key, Key);
    public string Display => Rules.Display(Key, Value);
    /// <summary>Значение нельзя применить в шаблоне (например, номер страницы вверху), но оно участвует в сравнении с ГОСТ.</summary>
    public bool IsInformational { get; init; }
}

public sealed class RulesDetection
{
    public required string Source { get; init; }
    /// <summary>Результат приблизительный (PDF, мало текста).</summary>
    public bool Approximate { get; init; }
    public List<DetectedRule> Rules { get; } = new();
    public StandardVerdict? Verdict { get; set; }

    public void Add(string key, object value, Confidence confidence, string evidence, bool informational = false)
    {
        Rules.RemoveAll(r => r.Key == key);
        Rules.Add(new DetectedRule(key, value, confidence, evidence) { IsInformational = informational });
    }

    public DetectedRule? this[string key] => Rules.FirstOrDefault(r => r.Key == key);
}

/// <summary>Справочник правил: названия и отображение значений (как в разделе «Правила оформления»).</summary>
public static class Rules
{
    public static readonly string[] Order =
    [
        "margins", "pageNumber", "font", "fontSize", "lineSpacing", "indent", "align", "hyphenation",
        "headingAlign", "chapterNewPage", "structuralUpper", "tocLevels", "figurePrefix", "separator", "tableCaptionAlign", "tableFontSize",
    ];

    public static readonly Dictionary<string, string> Names = new()
    {
        ["margins"] = "Поля страницы", ["pageNumber"] = "Номер страницы", ["font"] = "Шрифт", ["fontSize"] = "Размер шрифта",
        ["lineSpacing"] = "Межстрочный интервал", ["indent"] = "Абзацный отступ", ["align"] = "Выравнивание",
        ["hyphenation"] = "Автоматические переносы", ["headingAlign"] = "Положение заголовков глав",
        ["chapterNewPage"] = "Каждая глава с новой страницы", ["structuralUpper"] = "Структурные заголовки прописными",
        ["tocLevels"] = "Уровни в содержании", ["figurePrefix"] = "Подпись рисунка", ["separator"] = "Знак в подписи",
        ["tableCaptionAlign"] = "Подпись таблицы", ["tableFontSize"] = "Шрифт в таблицах",
    };

    /// <summary>Склонение: Plural(2, "заголовок", "заголовка", "заголовков") → «2 заголовка».</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        var word = mod10 == 1 && mod100 != 11 ? one : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? few : many;
        return $"{n} {word}";
    }

    public static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    public static string Display(string key, object value) => (key, value) switch
    {
        ("margins", MarginsValue m) => m.ToString(),
        ("pageNumber", string s) => s switch
        {
            "center" => "внизу по центру", "right" => "внизу справа", "top-center" => "вверху по центру", "top-right" => "вверху справа", _ => s,
        },
        ("fontSize" or "tableFontSize", double d) => $"{Num(d)} пт",
        ("lineSpacing", double d) => d.ToString("0.0#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")),
        ("indent", double d) => $"{Num(d)} см",
        ("align", string s) => s == "justify" ? "по ширине" : "по левому краю",
        ("headingAlign", string s) => s == "center" ? "по центру" : "с абзацного отступа",
        ("tableCaptionAlign", string s) => s == "center" ? "по центру" : "слева",
        ("separator", string s) => s.Trim() == "." ? "точка («Рисунок 1. Название»)" : "тире («Рисунок 1 – Название»)",
        (_, bool b) => b ? "да" : "нет",
        _ => value.ToString() ?? "",
    };
}
