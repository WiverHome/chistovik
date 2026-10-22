using ShablonRabot.Core;
using ShablonRabot.Core.Detection;

namespace ShablonRabot.Tests;

/// <summary>Определение правил и ГОСТа по примеру работы (.docx) и по тексту требований.</summary>
public class DetectionTests
{
    // ---------------- по примеру (.docx)

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void ПоШаблонуОпределяютсяЕгоЖеПравила(string id)
    {
        var p = BuiltInProfiles.Load(id);
        using var dir = new TempDir();
        var path = TestFiles.Generate(p, dir);

        var d = SampleAnalyzer.AnalyzeFile(path);
        Assert.NotNull(d.Verdict);

        var margins = Assert.IsType<MarginsValue>(d["margins"]?.Value);
        Assert.Equal(p.Page.MarginsMm.Left, margins.Left!.Value, 0.5);
        Assert.Equal(p.Page.MarginsMm.Right, margins.Right!.Value, 0.5);
        Assert.Equal(p.Page.MarginsMm.Top, margins.Top!.Value, 0.5);
        Assert.Equal(p.Page.MarginsMm.Bottom, margins.Bottom!.Value, 0.5);
        Assert.Equal(p.Text.Font, d["font"]?.Value);
        Assert.Equal(p.Text.SizePt, (double)d["fontSize"]!.Value, 0.01);
        Assert.Equal(p.Text.LineSpacing, (double)d["lineSpacing"]!.Value, 0.01);
        Assert.Equal(p.Text.FirstLineIndentCm, (double)d["indent"]!.Value, 0.01);
    }

    [Theory]
    [MemberData(nameof(TestFiles.ProfileIds), MemberType = typeof(TestFiles))]
    public void ПоШаблонуУзнаётсяЕгоГост(string id)
    {
        // Пояснительная записка — ЕСКД, диссертация — ГОСТ Р 7.0.11, остальные виды работ — ГОСТ 7.32-2017
        var standard = id switch { "eskd" => "gost-r-2.105-2019", "dissertation" => "gost-r-7.0.11-2011", _ => "gost-7.32-2017" };
        using var dir = new TempDir();
        var path = TestFiles.Generate(BuiltInProfiles.Load(id), dir);
        var v = SampleAnalyzer.AnalyzeFile(path).Verdict!;
        Assert.Equal(VerdictKind.Matches, v.Kind);
        Assert.Equal(standard, v.Standard!.Id);
    }

    [Fact]
    public void НеподдерживаемыйИлиОтсутствующийФайл()
    {
        using var dir = new TempDir();
        var missing = Assert.Throws<DetectionException>(() => SampleAnalyzer.AnalyzeFile(dir.File("нет.docx")));
        Assert.Equal(DetectionError.Unreadable, missing.Kind);

        var broken = dir.File("битый.docx");
        File.WriteAllText(broken, "это не docx");
        Assert.Equal(DetectionError.Unreadable, Assert.Throws<DetectionException>(() => SampleAnalyzer.AnalyzeFile(broken)).Kind);

        Assert.True(SampleAnalyzer.IsSupported("a.PDF"));
        Assert.True(SampleAnalyzer.NeedsWord("a.rtf"));
        Assert.False(SampleAnalyzer.IsSupported("a.txt"));
    }

    [Fact]
    public void ЗанятыйФайлСообщаетсяОтдельно()
    {
        using var dir = new TempDir();
        var path = TestFiles.Generate(BuiltInProfiles.Load("kursovaya"), dir);
        using var lockIt = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(DetectionError.Locked, Assert.Throws<DetectionException>(() => SampleAnalyzer.AnalyzeFile(path)).Kind);
    }

    // ---------------- по тексту требований

    [Fact]
    public void ТекстПоГост732()
    {
        var d = RequirementsParser.Parse(
            "Поля: левое — 30 мм, правое — 15 мм, верхнее и нижнее — 20 мм. Шрифт Times New Roman, 14 пт, " +
            "межстрочный интервал полуторный, абзацный отступ 1,25 см, выравнивание по ширине. " +
            "Номер страницы проставляется внизу по центру.");

        Assert.Equal(new MarginsValue(30, 15, 20, 20), d["margins"]?.Value);
        Assert.Equal("Times New Roman", d["font"]?.Value);
        Assert.Equal(14.0, d["fontSize"]?.Value);
        Assert.Equal(1.5, d["lineSpacing"]?.Value);
        Assert.Equal(1.25, d["indent"]?.Value);
        Assert.Equal("justify", d["align"]?.Value);
        Assert.Equal("center", d["pageNumber"]?.Value);
        Assert.Equal(VerdictKind.Matches, d.Verdict!.Kind);
        Assert.Same(Standards.Gost732, d.Verdict.Standard);
        Assert.Empty(d.Verdict.Differences);
    }

    [Fact]
    public void ТекстПоГостДиссертации()
    {
        var d = RequirementsParser.Parse(
            "Поля: левое 25 мм, правое 10 мм, верхнее 20 мм, нижнее 20 мм. Шрифт Times New Roman 14, интервал 1,5, " +
            "абзацный отступ 1,25 см. Номера страниц вверху по центру. Заголовки глав располагают по центру.");
        Assert.Equal(VerdictKind.Matches, d.Verdict!.Kind);
        Assert.Same(Standards.Gost7011, d.Verdict.Standard);
    }

    [Fact]
    public void ДесятичнаяТочкаНеЛомаетРазбор()
    {
        var d = RequirementsParser.Parse("Шрифт 14, интервал 1.5, отступ 1.25 см.");
        Assert.Equal(1.5, d["lineSpacing"]?.Value);
        Assert.Equal(1.25, d["indent"]?.Value);
    }

    [Fact]
    public void СвояМетодичкаНеВыдаётсяЗаГост()
    {
        var d = RequirementsParser.Parse(
            "Поля: левое 20 мм, правое 20 мм, верхнее 25 мм, нижнее 25 мм. Шрифт Arial 12, интервал одинарный, " +
            "абзацный отступ 1 см, текст выравнивается по левому краю. Номер страницы внизу справа.");
        Assert.Equal(VerdictKind.CustomRules, d.Verdict!.Kind);
        Assert.Equal("Своя методичка", d.Verdict.Title);
        Assert.NotEmpty(d.Verdict.Differences);
    }

    [Fact]
    public void МалоДанных()
    {
        var d = RequirementsParser.Parse("Шрифт 14.");
        Assert.Equal(VerdictKind.NotEnoughData, d.Verdict!.Kind);
        Assert.Empty(RequirementsParser.Parse("Привет! Работу сдать до пятницы.").Rules);
    }

    // ---------------- сравнение с эталонами

    static RulesDetection Rules(params (string Key, object Value)[] rules)
    {
        var d = new RulesDetection { Source = "тест" };
        foreach (var (k, v) in rules) d.Add(k, v, Confidence.High, "");
        return d;
    }

    [Fact]
    public void УстаревшийГост732_2001ПомечаетсяУстаревшим()
    {
        var v = Standards.Match(Rules(("margins", new MarginsValue(30, 10, 20, 20)), ("fontSize", 14.0), ("lineSpacing", 1.5),
            ("align", "justify"), ("pageNumber", "center"), ("headingAlign", "indent"), ("structuralUpper", true)));
        Assert.Equal(VerdictKind.Matches, v.Kind);
        Assert.Same(Standards.Gost732_2001, v.Standard);
        Assert.True(v.Standard!.IsOutdated);
        Assert.Contains("устарел", v.Title);
    }

    [Fact]
    public void ОбщиеПравилаНеДаютЭталонуСОтличительнымиПравиламиПобедить()
    {
        // Только общие для всех ГОСТов правила: ЕСКД и диссертация не должны «выиграть» без своих отличий
        var v = Standards.Match(Rules(("fontSize", 14.0), ("lineSpacing", 1.5), ("indent", 1.25), ("align", "justify"),
            ("figurePrefix", "Рисунок"), ("separator", " – ")));
        Assert.Same(Standards.Gost732, v.Standard);
    }

    [Fact]
    public void ПравилаНизкойУверенностиНеУчитываются()
    {
        var d = new RulesDetection { Source = "тест" };
        foreach (var (k, val) in new (string, object)[] { ("fontSize", 14.0), ("lineSpacing", 1.5), ("indent", 1.25), ("align", "justify"), ("pageNumber", "center"), ("headingAlign", "indent") })
            d.Add(k, val, Confidence.Low, "");
        Assert.Equal(VerdictKind.NotEnoughData, Standards.Match(d).Kind);
    }

    [Theory]
    [InlineData(1, "1 заголовок")]
    [InlineData(3, "3 заголовка")]
    [InlineData(11, "11 заголовков")]
    [InlineData(22, "22 заголовка")]
    [InlineData(25, "25 заголовков")]
    public void Склонение(int n, string expected) =>
        Assert.Equal(expected, ShablonRabot.Core.Detection.Rules.Plural(n, "заголовок", "заголовка", "заголовков"));
}
