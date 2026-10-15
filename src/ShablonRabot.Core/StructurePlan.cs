namespace ShablonRabot.Core;

/// <summary>
/// Упрощённая структура работы для редактора: какие разделы включены, главы и параграфы, число приложений.
/// Строится из профиля и обратно применяется к нему, сохраняя подсказки у существующих разделов.
/// </summary>
public sealed class StructurePlan
{
    /// <summary>Буквы приложений по ГОСТ 7.32 (без Ё, З, Й, О, Ч, Ъ, Ы, Ь).</summary>
    public static readonly string[] AppendixLetters =
        ["А", "Б", "В", "Г", "Д", "Е", "Ж", "И", "К", "Л", "М", "Н", "П", "Р", "С", "Т", "У", "Ф", "Х", "Ц", "Ш", "Щ", "Э", "Ю", "Я"];

    public const int MaxChapters = 10;
    public const int MaxSections = 10;
    public const int MaxAppendices = 10;

    public bool Toc { get; set; } = true;
    public bool Introduction { get; set; } = true;
    public bool Conclusion { get; set; } = true;
    public bool References { get; set; } = true;
    public int Appendices { get; set; } = 1;
    /// <summary>Образцы рисунка, таблицы, формулы и списков внутри параграфов.</summary>
    public bool Examples { get; set; } = true;
    public List<ChapterPlan> Chapters { get; set; } = new();

    static readonly BodyItem[] PlainSection = [BodyItem.Paragraph("[Текст параграфа.]")];

    public static StructurePlan From(Profile profile)
    {
        var s = profile.Structure;
        return new StructurePlan
        {
            Toc = s.Any(i => i.Type == "toc"),
            Introduction = s.Any(IsIntro),
            Conclusion = s.Any(IsConclusion),
            References = s.Any(i => i.Type == "references"),
            Appendices = s.Count(i => i.Type == "appendix"),
            Examples = s.Where(i => i.Type == "chapter").SelectMany(c => c.Sections ?? [])
                        .Any(x => x.Body?.Any(b => b.Kind != BodyKind.Text) == true),
            Chapters = s.Where(i => i.Type == "chapter")
                .Select(c => new ChapterPlan(c.Title, (c.Sections ?? []).Select(x => x.Title).ToList()))
                .ToList(),
        };
    }

    /// <summary>Пересобирает profile.Structure по плану. Тексты-подсказки берутся из исходного профиля (template).</summary>
    public void ApplyTo(Profile profile, Profile template)
    {
        var src = template.Structure;
        var result = new List<StructureItem>();
        StructureItem? Find(Func<StructureItem, bool> f) => src.FirstOrDefault(f);

        // Прочие структурные элементы профиля (аннотация, перечень сокращений…) остаются на своих местах
        var firstChapter = src.FindIndex(i => i.Type == "chapter");
        bool Extra(StructureItem i) => i.Type == "structural" && !IsIntro(i) && !IsConclusion(i);
        var extraBefore = src.Where((i, idx) => Extra(i) && (firstChapter < 0 || idx < firstChapter)).ToList();
        var extraAfter = src.Where((i, idx) => Extra(i) && firstChapter >= 0 && idx > firstChapter).ToList();

        if (Toc) result.Add(Find(i => i.Type == "toc") ?? new StructureItem { Type = "toc", Title = "Содержание" });
        result.AddRange(extraBefore);
        if (Introduction) result.Add(Find(IsIntro) ?? new StructureItem { Title = "Введение", Body = [BodyItem.Paragraph("[Текст введения.]")] });

        // Подсказки и примеры оформления из профиля раскладываются по параграфам по порядку;
        // если параграфов меньше, оставшиеся примеры попадают в последний.
        var srcBodies = src.Where(i => i.Type == "chapter").SelectMany(c => c.Sections ?? [])
            .Select(x => x.Body ?? []).ToList();
        var total = Chapters.Sum(c => c.Sections.Count);
        var index = 0;
        List<BodyItem> BodyFor(int i)
        {
            if (!Examples || srcBodies.Count == 0) return [.. PlainSection];
            if (i >= srcBodies.Count) return [.. PlainSection];
            return i == total - 1 ? srcBodies.Skip(i).SelectMany(b => b).ToList() : srcBodies[i];
        }
        foreach (var plan in Chapters)
        {
            result.Add(new StructureItem
            {
                Type = "chapter",
                Title = plan.Title,
                Sections = plan.Sections.Select(title => new Section { Title = title, Body = BodyFor(index++) }).ToList(),
            });
        }

        if (Conclusion) result.Add(Find(IsConclusion) ?? new StructureItem { Title = "Заключение", Body = [BodyItem.Paragraph("[Выводы по работе.]")] });
        result.AddRange(extraAfter);
        if (References) result.Add(Find(i => i.Type == "references") ?? new StructureItem { Type = "references", Title = "Список использованных источников", Items = ["[Источник.]"] });

        // Приложения профиля сохраняют свои заголовки и подсказки («Дневник практики»); новые — с заглушкой
        var srcAppendices = src.Where(i => i.Type == "appendix").ToList();
        for (var a = 0; a < Math.Min(Appendices, MaxAppendices); a++)
        {
            var own = a < srcAppendices.Count ? srcAppendices[a] : null;
            result.Add(new StructureItem
            {
                Type = "appendix",
                Letter = AppendixLetters[a],
                Title = own?.Title ?? "[Название приложения]",
                Body = own?.Body ?? [BodyItem.Paragraph("[Содержание приложения.]")],
            });
        }
        profile.Structure = result;
    }

    static bool IsIntro(StructureItem i) => i.Type == "structural" && i.Title.Equals("Введение", StringComparison.OrdinalIgnoreCase);
    static bool IsConclusion(StructureItem i) => i.Type == "structural" && i.Title.Equals("Заключение", StringComparison.OrdinalIgnoreCase);
}

public sealed record ChapterPlan(string Title, List<string> Sections);
