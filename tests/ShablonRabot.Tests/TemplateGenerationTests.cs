using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using ShablonRabot.Core;

namespace ShablonRabot.Tests;

/// <summary>Шаблон Word собирается по каждому встроенному виду работы и соответствует его правилам.</summary>
public class TemplateGenerationTests
{
    [Fact]
    public void ВсеДвенадцатьВидовРаботВстроены()
    {
        Assert.Equal(12, BuiltInProfiles.All.Length);
        Assert.Equal(BuiltInProfiles.All.Length, BuiltInProfiles.All.Select(p => p.Id).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void ПрофильЗагружаетсяСТитуломИСтруктурой(string id)
    {
        var p = BuiltInProfiles.Load(id);
        Assert.False(string.IsNullOrWhiteSpace(p.Name));
        Assert.NotEmpty(p.TitlePage.Blocks);
        Assert.NotEmpty(p.Structure);
        Assert.All(p.Structure, i => Assert.Contains(i.Type, new[] { "toc", "structural", "chapter", "references", "appendix" }));
        Assert.Equal("Times New Roman", p.Text.Font);
        Assert.InRange(p.Text.SizePt, 12, 14);
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void ШаблонПроходитПроверкуСхемыOpenXml(string id)
    {
        using var dir = new TempDir();
        var path = TestFiles.Generate(BuiltInProfiles.Load(id), dir);
        using var doc = WordprocessingDocument.Open(path, false);
        var errors = new OpenXmlValidator().Validate(doc).Select(e => $"{e.Description} @ {e.Path?.XPath}").ToList();
        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void ФорматЛистаИПоляКакВПрофиле(string id)
    {
        var p = BuiltInProfiles.Load(id);
        using var dir = new TempDir();
        var path = TestFiles.Generate(p, dir);
        using var doc = WordprocessingDocument.Open(path, false);
        var sect = doc.MainDocumentPart!.Document.Body!.Elements<SectionProperties>().Single();
        var size = sect.GetFirstChild<PageSize>()!;
        var m = sect.GetFirstChild<PageMargin>()!;
        Assert.Equal((uint)TestFiles.Mm(p.Page.WidthMm), size.Width!.Value);
        Assert.Equal((uint)TestFiles.Mm(p.Page.HeightMm), size.Height!.Value);
        Assert.Equal((uint)TestFiles.Mm(p.Page.MarginsMm.Left), m.Left!.Value);
        Assert.Equal((uint)TestFiles.Mm(p.Page.MarginsMm.Right), m.Right!.Value);
        Assert.Equal(TestFiles.Mm(p.Page.MarginsMm.Top), m.Top!.Value);
        Assert.Equal(TestFiles.Mm(p.Page.MarginsMm.Bottom), m.Bottom!.Value);
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void ВШаблонеВсеРазделыСтруктуры(string id)
    {
        var p = BuiltInProfiles.Load(id);
        using var dir = new TempDir();
        var path = TestFiles.Generate(p, dir);

        var chapters = p.Structure.Where(i => i.Type == "chapter").ToList();
        Assert.Equal(chapters.Count, TestFiles.CountStyle(path, "Heading1"));
        Assert.Equal(chapters.Sum(c => c.Sections?.Count ?? 0), TestFiles.CountStyle(path, "Heading2"));
        Assert.Equal(p.Structure.Any(i => i.Type == "toc"), TestFiles.HasTocField(path));

        var texts = TestFiles.ParagraphTexts(path);
        foreach (var a in p.Structure.Where(i => i.Type == "appendix"))
            Assert.Contains(texts, t => t.Equals($"Приложение {a.Letter}", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void ПоляТитулаПодставляются(string id)
    {
        var p = BuiltInProfiles.Load(id);
        p.Fields["topic"] = "Проверочная тема 12345";
        p.Fields["student"] = "Иванов И. И. 67890";
        using var dir = new TempDir();
        var path = TestFiles.Generate(p, dir);
        var texts = string.Join("\n", TestFiles.ParagraphTexts(path));
        Assert.Contains("Иванов И. И. 67890", texts);
        // Темы нет на титуле отчёта по практике и контрольной — так задумано в их профилях
        var titleHasTopic = p.TitlePage.Blocks.SelectMany(b => b.Lines.Concat(b.Rows.SelectMany(r => r))).Any(l => l.Contains("{topic}"));
        if (titleHasTopic) Assert.Contains("Проверочная тема 12345", texts);
        // Неподставленных {полей} в документе не остаётся там, где поле задано в профиле
        foreach (var key in p.VisibleFields.Select(f => f.Key))
            Assert.DoesNotContain("{" + key + "}", texts);
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.Equal("Проверочная тема 12345", doc.PackageProperties.Title);
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void СборкаЧерезTemplateServiceБезWord(string id)
    {
        using var dir = new TempDir();
        var path = dir.File("out.docx");
        var result = TemplateService.Generate(BuiltInProfiles.Load(id), path, pdf: false, useWord: false);
        Assert.Equal(WordIssue.Disabled, result.WordIssue);
        Assert.True(File.Exists(path));
        // Временный файл сборки («~….docx») не остаётся рядом
        Assert.Single(Directory.GetFiles(dir.Path));
    }
}
