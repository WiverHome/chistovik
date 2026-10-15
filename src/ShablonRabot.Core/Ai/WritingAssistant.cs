using System.Text;
using System.Text.RegularExpressions;

namespace ShablonRabot.Core.Ai;

/// <summary>Что попросить у ИИ.</summary>
public enum WritingTask { Plan, Introduction, Section, Conclusion, Improve, References, Question }

/// <summary>Объём черновика: примерно страница, две-три или четыре-пять (14 пт, полуторный интервал — около 280 слов на странице).</summary>
public enum DraftLength { Short, Medium, Long }

/// <summary>О чём работа: вид, тема, дисциплина и план — из титульного листа и структуры.</summary>
public sealed record WorkContext
{
    public string WorkType { get; init; } = "";
    /// <summary>vo — вуз, spo — колледж или техникум.</summary>
    public string Level { get; init; } = "vo";
    public string Gost { get; init; } = "ГОСТ 7.32-2017";
    public string? Topic { get; init; }
    public string? Discipline { get; init; }
    public string? Specialty { get; init; }
    /// <summary>Пожелания студента: на чьём примере, что важно преподавателю.</summary>
    public string? Notes { get; init; }
    public IReadOnlyList<ChapterPlan> Chapters { get; init; } = [];
}

/// <summary>Запрос к ИИ: системная инструкция и сообщение.</summary>
public sealed record WritingPrompt(string System, string User);

/// <summary>
/// Подсказки для ИИ по задачам студента и разбор ответов: план → главы и параграфы, черновик → абзацы шаблона.
/// Главное правило подсказок — не выдумывать факты и источники: на их месте ИИ ставит пометку в [скобках].
/// </summary>
public static partial class WritingAssistant
{
    public static int Words(DraftLength length) => length switch { DraftLength.Short => 300, DraftLength.Long => 1300, _ => 700 };

    public static WritingPrompt Build(WritingTask task, WorkContext w, string? input = null, (int Chapter, int Section)? target = null, DraftLength length = DraftLength.Medium)
    {
        var words = Words(length);
        var user = task switch
        {
            WritingTask.Plan =>
                "Составь план основной части работы: " + PlanSize(w) + " Названия — конкретные, по теме, без слов «Глава» и «Параграф». " +
                "Обычно первая глава теоретическая, вторая — анализ или практическая часть" + (w.WorkType.Contains("еферат") ? " (в реферате обе главы — обзор источников)" : "") + ".\n" +
                "Ответь строго в формате, без введения, заключения и пояснений:\n1. Название главы\n1.1. Название параграфа\n1.2. Название параграфа\n2. Название главы\n2.1. Название параграфа",
            WritingTask.Introduction =>
                $"Напиши введение объёмом около {words} слов. " + (IsSmall(w)
                    ? "Включи: актуальность темы, цель, задачи (по одной на каждый параграф плана), структуру работы."
                    : "Включи: актуальность темы, степень изученности (без выдуманных фамилий — если нужно назвать авторов, оставь пометку [назвать авторов]), " +
                      "цель, задачи (по одной на каждый параграф плана), объект, предмет, методы исследования, структуру работы.") +
                " Цель и задачи сформулируй глаголами: «изучить», «проанализировать», «разработать».",
            WritingTask.Section when target is { } t && t.Chapter < w.Chapters.Count && t.Section < w.Chapters[t.Chapter].Sections.Count =>
                $"Напиши текст параграфа {t.Chapter + 1}.{t.Section + 1} «{w.Chapters[t.Chapter].Sections[t.Section]}» " +
                $"главы {t.Chapter + 1} «{w.Chapters[t.Chapter].Title}» объёмом около {words} слов. " +
                "Раскрой именно этот параграф, не повторяй содержание соседних. В конце — короткий вывод по параграфу." +
                (string.IsNullOrWhiteSpace(input) ? "" : "\nЧто учесть: " + input.Trim()),
            WritingTask.Section => throw new ArgumentException("Не выбран параграф", nameof(target)),
            WritingTask.Conclusion =>
                $"Напиши заключение объёмом около {words} слов: выводы по каждой главе и параграфу плана, итог — достигнута ли цель работы. " +
                "Не добавляй новых сведений, которых нет в плане.",
            WritingTask.Improve =>
                "Отредактируй текст студента ниже: научный стиль, без разговорных оборотов, повторов и «воды», исправь орфографию и пунктуацию. " +
                "Сохрани смысл, факты, цифры и ссылки вида [1]; ничего не добавляй от себя. Верни только исправленный текст.\n\nТекст:\n" + input?.Trim(),
            WritingTask.References =>
                "Оформи список источников по ГОСТ Р 7.0.100-2018. Каждый источник — отдельной строкой, без нумерации. " +
                "Сначала нормативные акты, затем остальные по алфавиту: сначала на русском, потом на иностранных языках. " +
                "Для сайтов добавь «URL: … (дата обращения: ДД.ММ.ГГГГ)». Если не хватает данных (город, издательство, год, страницы) — поставь [?] на их месте, не выдумывай. " +
                "Не добавляй источников, которых нет в списке. Верни только список.\n\nИсточники:\n" + input?.Trim(),
            _ => input?.Trim() ?? "",
        };
        return new WritingPrompt(SystemPrompt(w, task), user);
    }

    static bool IsSmall(WorkContext w) => w.Level == "spo" || w.WorkType.Contains("еферат") || w.WorkType.Contains("онтрольн") || w.WorkType.Contains("ндивидуальн");

    static string PlanSize(WorkContext w) =>
        w.WorkType.Contains("ыпускн") || w.WorkType.Contains("ипломн") || w.WorkType.Contains("иссертац")
            ? "3 главы, в каждой 2–4 параграфа."
            : "2 главы, в каждой 2–3 параграфа.";

    static string SystemPrompt(WorkContext w, WritingTask task)
    {
        var sb = new StringBuilder();
        var who = w.Level == "spo" ? "студенту колледжа" : "студенту вуза";
        sb.Append($"Ты помогаешь {who} с учебной работой: {Lower(w.WorkType)}");
        if (!string.IsNullOrWhiteSpace(w.Topic)) sb.Append($" на тему «{Quote(w.Topic)}»");
        if (!string.IsNullOrWhiteSpace(w.Discipline)) sb.Append($" по дисциплине «{Quote(w.Discipline)}»");
        if (!string.IsNullOrWhiteSpace(w.Specialty)) sb.Append($", специальность {w.Specialty.Trim()}");
        sb.Append(". Оформление — по ").Append(w.Gost).Append(".\n");
        if (!string.IsNullOrWhiteSpace(w.Notes)) sb.Append("Пожелания студента: ").Append(w.Notes.Trim()).Append('\n');
        if (task != WritingTask.Plan && w.Chapters.Count > 0)
        {
            sb.Append("План основной части:\n");
            for (var i = 0; i < w.Chapters.Count; i++)
            {
                sb.Append($"{i + 1}. {Title(w.Chapters[i].Title, $"Глава {i + 1}")}\n");
                for (var j = 0; j < w.Chapters[i].Sections.Count; j++)
                    sb.Append($"{i + 1}.{j + 1}. {Title(w.Chapters[i].Sections[j], $"Параграф {i + 1}.{j + 1}")}\n");
            }
        }
        sb.Append("""

            Правила ответа:
            – Пиши по-русски, научным стилем, безлично («в работе рассмотрено», «проведён анализ»), без обращений к читателю.
            – Не выдумывай факты, статистику, цитаты, фамилии, законы и источники. Если нужны конкретные данные — оставь пометку в квадратных скобках, например [привести данные за последний год] или [ссылка на источник].
            – Без Markdown: никаких звёздочек, решёток и таблиц. Абзацы разделяй пустой строкой, перечисления — отдельными строками, начинающимися с «– ».
            – Не повторяй заголовок раздела и не пиши вступлений вроде «Вот текст». Сразу ответ.
            – Текст — черновик, который студент проверит и перепишет своими словами.
            """);
        return sb.ToString();
    }

    static string Title(string title, string fallback) => Placeholders.IsPlaceholder(title) ? fallback : title.Trim();
    static string Quote(string s) => Unquote(s.Trim());

    /// <summary>Снимает кавычки, только если в них взят весь текст: «Анализ ООО «Север»» → Анализ ООО «Север».</summary>
    public static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '«' && s[^1] == '»' && s.Count(ch => ch == '«') == s.Count(ch => ch == '»') && !Regex.IsMatch(s[1..^1], "^[^«]*»"))
                              || (s[0] == '"' && s[^1] == '"')))
            return s[1..^1].Trim();
        return s;
    }
    static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLower(s[0]) + s[1..] : s;

    // ------------------------------------------------------------ разбор ответа

    [GeneratedRegex(@"^\s*(?:глава\s+)?(\d{1,2})\s*[.)]?\s+(?!\d)(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterLine();
    [GeneratedRegex(@"^\s*(?:параграф\s+)?(\d{1,2})\.(\d{1,2})\.?\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionLine();

    /// <summary>План из ответа «1. Глава / 1.1. Параграф». Пустой список — ответ не похож на план.</summary>
    public static List<ChapterPlan> ParsePlan(string text)
    {
        var chapters = new List<ChapterPlan>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = StripMarkdown(raw).Trim();
            if (line.Length == 0) continue;
            if (SectionLine().Match(line) is { Success: true } s)
            {
                if (chapters.Count == 0) chapters.Add(new ChapterPlan("", []));
                if (chapters[^1].Sections.Count < StructurePlan.MaxSections) chapters[^1].Sections.Add(CleanTitle(s.Groups[3].Value));
            }
            else if (ChapterLine().Match(line) is { Success: true } c && chapters.Count < StructurePlan.MaxChapters)
                chapters.Add(new ChapterPlan(CleanTitle(c.Groups[2].Value), []));
        }
        return chapters.Where(c => c.Title.Length > 0 || c.Sections.Count > 0).ToList();
    }

    static string CleanTitle(string s)
    {
        s = Unquote(s.Trim());
        if (s.EndsWith('.') && !s.EndsWith("..")) s = s[..^1];
        return s.Length > 300 ? s[..300] : s;
    }

    [GeneratedRegex(@"^\s*[–\-—•*]\s+(.+)$")]
    private static partial Regex DashLine();
    [GeneratedRegex(@"^\s*\d{1,2}[).]\s+(.+)$")]
    private static partial Regex NumLine();
    [GeneratedRegex(@"\*\*|__|^#{1,6}\s+|`")]
    private static partial Regex Markdown();

    static string StripMarkdown(string s) => Markdown().Replace(s, "");

    /// <summary>Черновик → абзацы и списки шаблона. Строки «– …» — маркированный список, «1) …» — нумерованный.</summary>
    public static List<BodyItem> ToBody(string text)
    {
        var body = new List<BodyItem>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = StripMarkdown(raw).Trim();
            if (line.Length == 0) continue;
            if (DashLine().Match(line) is { Success: true } d) Add(BodyKind.Dash, d.Groups[1].Value);
            else if (NumLine().Match(line) is { Success: true } n) Add(BodyKind.Num, n.Groups[1].Value);
            else body.Add(BodyItem.Paragraph(line));
        }
        return body;

        void Add(BodyKind kind, string item)
        {
            if (body.Count > 0 && body[^1].Kind == kind) body[^1].Items.Add(item.Trim());
            else body.Add(new BodyItem { Kind = kind, Items = [item.Trim()] });
        }
    }

    /// <summary>Список литературы → строки без нумерации.</summary>
    public static List<string> ToReferences(string text) =>
        text.Replace("\r", "").Split('\n')
            .Select(l => Regex.Replace(StripMarkdown(l).Trim(), @"^(\d{1,3}[.)]|[–\-—•*])\s+", ""))
            .Where(l => l.Length > 0)
            .ToList();

    public static int CountWords(string text) => text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
}

/// <summary>
/// Черновики разделов, написанные с ИИ: при создании шаблона текст занимает место подсказок.
/// Ключи: «intro», «conclusion», «references», «section:Название параграфа».
/// </summary>
public static class Drafts
{
    public const string Intro = "intro", Conclusion = "conclusion", References = "references";
    public static string Section(string title) => "section:" + title.Trim();

    public static void ApplyTo(Profile profile, IReadOnlyDictionary<string, string>? drafts)
    {
        if (drafts == null || drafts.Count == 0) return;
        foreach (var item in profile.Structure)
        {
            switch (item.Type)
            {
                case "structural" when Is(item, "Введение") && drafts.TryGetValue(Intro, out var intro):
                    item.Body = WritingAssistant.ToBody(intro);
                    break;
                case "structural" when Is(item, "Заключение") && drafts.TryGetValue(Conclusion, out var end):
                    item.Body = WritingAssistant.ToBody(end);
                    break;
                case "references" when drafts.TryGetValue(References, out var refs) && WritingAssistant.ToReferences(refs) is { Count: > 0 } list:
                    item.Items = list;
                    break;
                case "chapter":
                    foreach (var sec in item.Sections ?? [])
                        if (drafts.TryGetValue(Section(sec.Title), out var text)) sec.Body = WritingAssistant.ToBody(text);
                    break;
            }
        }
    }

    static bool Is(StructureItem item, string title) => item.Title.Equals(title, StringComparison.OrdinalIgnoreCase);
}
