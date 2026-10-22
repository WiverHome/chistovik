using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using ShablonRabot.Core;
using ShablonRabot.Core.Checking;
using ShablonRabot.Core.Detection;

namespace ShablonRabot.Tests;

/// <summary>Проверка готовой работы: свой шаблон проходит, типичные ошибки оформления находятся.</summary>
public class CheckingTests
{
    static CheckReport Check(string path, Profile rules) => DocumentChecker.CheckFile(path, rules, rules.Name);

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void СвойШаблонБезОшибок(string id)
    {
        var p = BuiltInProfiles.Load(id);
        using var dir = new TempDir();
        var report = Check(TestFiles.Generate(p, dir), p);
        var errors = report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => $"{i.Key}: {i.Title} — {i.Detail}");
        Assert.Empty(errors);
        Assert.Contains("Поля страницы", report.Passed);
        Assert.Contains("Формат листа А4", report.Passed);
        Assert.True(report.BodyParagraphCount > 0);
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void СвойШаблонБезЗамечанийКОформлениюТекстаИЗаголовков(string id)
    {
        var p = BuiltInProfiles.Load(id);
        using var dir = new TempDir();
        var report = Check(TestFiles.Generate(p, dir), p);
        // Подсказки-заглушки («[Текст параграфа.]», источники без дат) могут давать замечания по содержанию,
        // но оформление, которое задаёт сам шаблон, должно соответствовать своим правилам
        string[] formatting = ["pageSize", "margins", "pageNumber", "titleNumber", "font", "fontSize", "lineSpacing", "indent", "align",
                               "structuralCase", "structuralAlign", "headingAlign", "headingDot", "newPage", "headingStyle", "toc",
                               // примеры в шаблоне сами должны быть написаны по ГОСТ 8.417 и Р 7.0.100
                               "unitSpace", "unitSymbol", "refPages", "refYear"];
        var issues = report.Issues.Where(i => formatting.Contains(i.Key)).Select(i => $"{i.Key}: {i.Title} — {i.Detail}");
        Assert.Empty(issues);
    }

    /// <summary>Портит готовый шаблон: поля, шрифт и размер основного текста.</summary>
    static void Spoil(string path)
    {
        using var doc = WordprocessingDocument.Open(path, true);
        var main = doc.MainDocumentPart!;
        var margin = main.Document.Body!.Elements<SectionProperties>().Single().GetFirstChild<PageMargin>()!;
        margin.Left = (uint)TestFiles.Mm(20);
        margin.Right = (uint)TestFiles.Mm(20);
        foreach (var f in main.StyleDefinitionsPart!.Styles!.Descendants<RunFonts>())
        {
            f.Ascii = "Arial"; f.HighAnsi = "Arial"; f.ComplexScript = "Arial"; f.EastAsia = "Arial";
            f.AsciiTheme = null; f.HighAnsiTheme = null;
        }
        var normal = main.StyleDefinitionsPart.Styles.Elements<Style>().Single(s => s.StyleId == "Normal");
        var rpr = normal.StyleRunProperties ??= new StyleRunProperties();
        rpr.FontSize = new FontSize { Val = "20" };
        rpr.FontSizeComplexScript = new FontSizeComplexScript { Val = "20" };
    }

    [Fact]
    public void НаходятсяНеверныеПоляШрифтИРазмер()
    {
        var p = BuiltInProfiles.Load("kursovaya");
        using var dir = new TempDir();
        var path = TestFiles.Generate(p, dir);
        Spoil(path);

        var report = Check(path, p);
        var keys = report.Issues.Select(i => i.Key).ToList();
        Assert.Contains("margins", keys);
        Assert.Contains("font", keys);
        Assert.Contains("fontSize", keys);
        Assert.Equal(IssueSeverity.Error, report.Issues.First(i => i.Key == "margins").Severity);
        Assert.DoesNotContain("Поля страницы", report.Passed);
        // Ошибки идут первыми
        Assert.Equal(report.Issues.OrderBy(i => i.Severity).Select(i => i.Severity), report.Issues.Select(i => i.Severity));
        Assert.StartsWith("Найдено:", report.Summary);
    }

    [Fact]
    public void КопияСПримечаниямиНеМеняетИсходник()
    {
        var p = BuiltInProfiles.Load("kursovaya");
        using var dir = new TempDir();
        var path = TestFiles.Generate(p, dir);
        Spoil(path);
        var before = File.ReadAllBytes(path);
        var report = Check(path, p);

        var copy = dir.File("с примечаниями.docx");
        var n = ReportAnnotator.SaveAnnotated(path, report, copy);
        Assert.True(n > 0);
        Assert.Equal(before, File.ReadAllBytes(path));
        using var doc = WordprocessingDocument.Open(copy, false);
        Assert.Equal(n, doc.MainDocumentPart!.WordprocessingCommentsPart!.Comments.ChildElements.Count);
        Assert.Empty(new OpenXmlValidator().Validate(doc));
    }

    [Fact]
    public void ЗаголовокТочкойИРучноеСодержаниеНаходятся()
    {
        var p = BuiltInProfiles.Load("kursovaya");
        using var dir = new TempDir();
        var path = TestFiles.Generate(p, dir);
        using (var doc = WordprocessingDocument.Open(path, true))
        {
            var body = doc.MainDocumentPart!.Document.Body!;
            // Автособираемое содержание заменяем текстом «вручную», к первой главе добавляем точку в конце
            foreach (var f in body.Descendants<FieldCode>().Where(f => f.Text.TrimStart().StartsWith("TOC")).ToList())
                f.Parent!.Parent!.Remove();
            var h1 = body.Descendants<Paragraph>().First(x => x.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "Heading1");
            h1.Descendants<Text>().Last().Text += ".";
        }
        var keys = Check(path, p).Issues.Select(i => i.Key).ToList();
        Assert.Contains("headingDot", keys);
        Assert.Contains("toc", keys);
    }

    [Fact]
    public void ПовреждённыйФайлНеОстаётсяЗанятым()
    {
        // После неудачной проверки или определения файл можно сразу переименовать или удалить
        var p = BuiltInProfiles.Load("kursovaya");
        using var dir = new TempDir();
        var docx = dir.File("битый.docx");
        File.WriteAllText(docx, "не docx");
        Assert.Throws<DetectionException>(() => Check(docx, p));
        Assert.Throws<DetectionException>(() => SampleAnalyzer.AnalyzeFile(docx));
        File.Delete(docx);
        Assert.False(File.Exists(docx));

        var pdf = dir.File("битый.pdf");
        File.WriteAllText(pdf, "не pdf");
        Assert.Throws<DetectionException>(() => SampleAnalyzer.AnalyzeFile(pdf));
        File.Delete(pdf);
        Assert.False(File.Exists(pdf));
    }

    [Fact]
    public void ОшибкиЧтенияФайла()
    {
        var p = BuiltInProfiles.Load("kursovaya");
        using var dir = new TempDir();
        Assert.Equal(DetectionError.Unreadable, Assert.Throws<DetectionException>(() => Check(dir.File("нет.docx"), p)).Kind);

        var pdf = dir.File("работа.pdf");
        File.WriteAllText(pdf, "%PDF-1.4");
        Assert.Equal(DetectionError.Unreadable, Assert.Throws<DetectionException>(() => Check(pdf, p)).Kind);

        var broken = dir.File("битый.docx");
        File.WriteAllText(broken, "не docx");
        Assert.Equal(DetectionError.Unreadable, Assert.Throws<DetectionException>(() => Check(broken, p)).Kind);
    }
}
