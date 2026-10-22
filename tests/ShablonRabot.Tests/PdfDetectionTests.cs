using ShablonRabot.Core.Detection;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ShablonRabot.Tests;

/// <summary>Определение правил по PDF (PdfPig): поля, кегль, интервал, отступ, номера страниц.</summary>
public class PdfDetectionTests
{
    const double PtPerMm = 72 / 25.4;

    /// <summary>
    /// Три страницы А4: левое поле 30 мм, верх 20 мм, кегль 14, интервал 1,5 (14 × 1,15 × 1,5 пт),
    /// каждая пятая строка — с абзацным отступом 1,25 см, номер страницы внизу по центру.
    /// </summary>
    static string MakePdf(TempDir dir)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        const double size = 14, step = size * 1.15 * 1.5;
        for (var n = 1; n <= 3; n++)
        {
            var page = builder.AddPage(PageSize.A4);
            var height = page.PageSize.Height;
            var y = height - 20 * PtPerMm - size * 0.72;
            for (var i = 0; i < 27; i++, y -= step)
            {
                var x = 30 * PtPerMm + (i % 5 == 0 ? 12.5 * PtPerMm : 0);
                page.AddText($"Line {i + 1} of the sample text that is long enough to be body text.", size, new PdfPoint(x, y), font);
            }
            page.AddText(n.ToString(), 12, new PdfPoint(page.PageSize.Width / 2 - 3, 12 * PtPerMm), font);
        }
        var path = dir.File("пример.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    [Fact]
    public void ПравилаОпределяютсяПоPdf()
    {
        using var dir = new TempDir();
        var d = SampleAnalyzer.AnalyzeFile(MakePdf(dir));

        Assert.True(d.Approximate);
        var m = Assert.IsType<MarginsValue>(d["margins"]?.Value);
        Assert.InRange(m.Left!.Value, 29, 31);
        Assert.InRange(m.Top!.Value, 18, 22);
        Assert.Equal(14.0, d["fontSize"]?.Value);
        Assert.Equal("Helvetica", d["font"]?.Value);
        Assert.Equal(1.5, d["lineSpacing"]?.Value);
        Assert.Equal(1.25, d["indent"]?.Value);
        Assert.Equal("center", d["pageNumber"]?.Value);
        Assert.NotNull(d.Verdict);
    }
}
