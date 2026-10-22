using ShablonRabot.Core;
using ShablonRabot.Core.Checking;

namespace ShablonRabot.Tests;

/// <summary>Редактор структуры: отключаемое содержание (1.4.2), главы и приложения.</summary>
public class StructureTests
{
    static Profile WithPlan(string id, Action<StructurePlan> change)
    {
        var template = BuiltInProfiles.Load(id);
        var profile = template.Clone();
        var plan = StructurePlan.From(profile);
        change(plan);
        plan.ApplyTo(profile, template);
        return profile;
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void ПланБезИзмененийНеМеняетСтруктуру(string id)
    {
        var template = BuiltInProfiles.Load(id);
        var profile = WithPlan(id, _ => { });
        Assert.Equal(template.Structure.Select(i => (i.Type, i.Title)), profile.Structure.Select(i => (i.Type, i.Title)));
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void СодержаниеОтключаетсяУЛюбогоВидаРаботы(string id)
    {
        var template = BuiltInProfiles.Load(id);
        var profile = WithPlan(id, p => p.Toc = false);

        Assert.DoesNotContain(profile.Structure, i => i.Type == "toc");
        // Остальное на месте
        Assert.Equal(template.Structure.Where(i => i.Type != "toc").Select(i => (i.Type, i.Title)),
                     profile.Structure.Select(i => (i.Type, i.Title)));
        Assert.False(StructurePlan.From(profile).Toc);

        using var dir = new TempDir();
        var path = TestFiles.Generate(profile, dir);
        Assert.False(TestFiles.HasTocField(path));
        var tocTitle = template.Structure.FirstOrDefault(i => i.Type == "toc")?.Title ?? "Содержание";
        Assert.DoesNotContain(TestFiles.ParagraphTexts(path), t => t.Trim().Equals(tocTitle, StringComparison.OrdinalIgnoreCase));

        // Работа без содержания по правилам без содержания — без замечания «Нет содержания»
        var report = DocumentChecker.CheckFile(path, profile, profile.Name);
        Assert.DoesNotContain(report.Issues, i => i.Key == "toc");
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void СодержаниеВозвращаетсяНаПервоеМесто(string id)
    {
        var template = BuiltInProfiles.Load(id);
        var off = WithPlan(id, p => p.Toc = false);
        var plan = StructurePlan.From(off);
        plan.Toc = true;
        plan.ApplyTo(off, template);

        Assert.Equal("toc", off.Structure[0].Type);
        var own = template.Structure.FirstOrDefault(i => i.Type == "toc");
        Assert.Equal(own?.Title ?? "Содержание", off.Structure[0].Title);

        using var dir = new TempDir();
        Assert.True(TestFiles.HasTocField(TestFiles.Generate(off, dir)));
    }

    [Fact]
    public void ПроверкаПоПравиламССодержаниемЗамечаетЕгоОтсутствие()
    {
        var withToc = BuiltInProfiles.Load("kursovaya");
        var withoutToc = WithPlan("kursovaya", p => p.Toc = false);
        using var dir = new TempDir();
        var path = TestFiles.Generate(withoutToc, dir);
        var report = DocumentChecker.CheckFile(path, withToc, withToc.Name);
        Assert.Contains(report.Issues, i => i.Key == "toc" && i.Title == "Нет содержания");
    }

    [Fact]
    public void ГлавыПараграфыИПриложения()
    {
        var profile = WithPlan("vkr", p =>
        {
            p.Chapters = [new ChapterPlan("Теория", ["Обзор", "Термины"]), new ChapterPlan("Практика", ["Расчёт"]), new ChapterPlan("Итоги", ["Выводы"])];
            p.Appendices = 3;
            p.Introduction = false;
        });
        var chapters = profile.Structure.Where(i => i.Type == "chapter").ToList();
        Assert.Equal(["Теория", "Практика", "Итоги"], chapters.Select(c => c.Title));
        Assert.Equal(["Обзор", "Термины"], chapters[0].Sections!.Select(s => s.Title));
        Assert.Equal(["А", "Б", "В"], profile.Structure.Where(i => i.Type == "appendix").Select(a => a.Letter!));
        Assert.DoesNotContain(profile.Structure, i => i.Title.Equals("Введение", StringComparison.OrdinalIgnoreCase));

        using var dir = new TempDir();
        var path = TestFiles.Generate(profile, dir);
        Assert.Equal(3, TestFiles.CountStyle(path, "Heading1"));
        Assert.Equal(4, TestFiles.CountStyle(path, "Heading2"));
    }

    [Fact]
    public void БезПримеровОформленияТолькоТекст()
    {
        var profile = WithPlan("kursovaya", p => p.Examples = false);
        var bodies = profile.Structure.Where(i => i.Type == "chapter").SelectMany(c => c.Sections!).SelectMany(s => s.Body!);
        Assert.All(bodies, b => Assert.Equal(BodyKind.Text, b.Kind));
        Assert.False(StructurePlan.From(profile).Examples);
    }

    [Fact]
    public void ЧислоПриложенийОграничено()
    {
        var profile = WithPlan("kursovaya", p => p.Appendices = 50);
        Assert.Equal(StructurePlan.MaxAppendices, profile.Structure.Count(i => i.Type == "appendix"));
        // Буквы по ГОСТ 7.32: без Ё, З, Й, О, Ч, Ъ, Ы, Ь
        Assert.DoesNotContain(profile.Structure.Where(i => i.Type == "appendix"), a => "ЁЗЙОЧЪЫЬ".Contains(a.Letter!));
    }
}
