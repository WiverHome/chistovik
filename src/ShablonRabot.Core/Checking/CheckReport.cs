namespace ShablonRabot.Core.Checking;

/// <summary>Ошибка — нормоконтроль точно не пропустит; замечание — скорее всего вернут; совет — желательно поправить.</summary>
public enum IssueSeverity { Error, Warning, Advice }

/// <summary>Где найдено: номер абзаца (для примечания Word), раздел работы и начало абзаца.</summary>
public sealed record IssueLocation(int ParagraphIndex, string Section, string Snippet);

public sealed class CheckIssue
{
    public required string Key { get; init; }
    public required string Category { get; init; }
    public required IssueSeverity Severity { get; init; }
    /// <summary>Что не так — коротко: «Шрифт не Times New Roman».</summary>
    public required string Title { get; init; }
    /// <summary>Подробности: сколько, что найдено и что требуется.</summary>
    public string Detail { get; init; } = "";
    /// <summary>Как исправить в Word.</summary>
    public string? Fix { get; init; }
    /// <summary>Пункт стандарта или правило профиля.</summary>
    public string? Source { get; init; }
    public List<IssueLocation> Locations { get; } = [];
    /// <summary>Всего мест (в Locations хранятся первые из них).</summary>
    public int Count { get; set; }
}

public sealed class CheckReport
{
    public required string FileName { get; init; }
    /// <summary>По каким правилам проверяли: «Курсовая работа (ГОСТ 7.32-2017)».</summary>
    public required string RulesName { get; init; }
    public List<CheckIssue> Issues { get; } = [];
    /// <summary>Проверки без замечаний — чтобы было видно, что проверено.</summary>
    public List<string> Passed { get; } = [];
    public int ParagraphCount { get; set; }
    public int BodyParagraphCount { get; set; }

    public int Errors => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int Warnings => Issues.Count(i => i.Severity == IssueSeverity.Warning);
    public int Advices => Issues.Count(i => i.Severity == IssueSeverity.Advice);

    public string Summary => Issues.Count == 0
        ? "Замечаний нет: оформление соответствует правилам."
        : $"Найдено: {Plural(Errors, "ошибка", "ошибки", "ошибок")}, {Plural(Warnings, "замечание", "замечания", "замечаний")}, {Plural(Advices, "совет", "совета", "советов")}.";

    internal static string Plural(int n, string one, string few, string many) => Detection.Rules.Plural(n, one, few, many);
}
