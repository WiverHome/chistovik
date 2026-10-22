using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ShablonRabot.Core;

namespace ShablonRabot.Tests;

/// <summary>Временная папка теста; удаляется вместе с содержимым.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chistovik-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public static class TestFiles
{
    /// <summary>Все встроенные виды работы — для параметризованных тестов.</summary>
    public static IEnumerable<object[]> ProfileIds => BuiltInProfiles.All.Select(p => new object[] { p.Id });

    /// <summary>Собирает шаблон без Word (поля обновятся при открытии) — как в CI.</summary>
    public static string Generate(Profile profile, TempDir dir, string name = "work.docx")
    {
        var path = dir.File(name);
        TemplateGenerator.Generate(profile, path, updateFieldsOnOpen: true);
        return path;
    }

    public static bool HasTocField(string docx)
    {
        using var doc = WordprocessingDocument.Open(docx, false);
        return doc.MainDocumentPart!.Document.Body!.Descendants<FieldCode>()
            .Any(f => f.Text.TrimStart().StartsWith("TOC", StringComparison.OrdinalIgnoreCase));
    }

    public static List<string> ParagraphTexts(string docx)
    {
        using var doc = WordprocessingDocument.Open(docx, false);
        return doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>()
            .Select(p => string.Concat(p.Descendants<Text>().Select(t => t.Text))).ToList();
    }

    public static int CountStyle(string docx, string styleId)
    {
        using var doc = WordprocessingDocument.Open(docx, false);
        return doc.MainDocumentPart!.Document.Body!.Descendants<Paragraph>()
            .Count(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value == styleId);
    }

    public static int Mm(double mm) => (int)Math.Round(mm * 56.6929);
}
