using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ShablonRabot.Core;
using ShablonRabot.Core.Checking;

namespace ShablonRabot.Tests;

/// <summary>Проверки по ГОСТ 8.417 (единицы величин), Р 7.0.12 и Р 7.0.100 (сокращения в списке), 7.32 и Р 7.0.99 (реферат).</summary>
public class GostChecksTests
{
    static Paragraph Styled(string style, string text) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = style }), new Run(new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }));

    /// <summary>Шаблон курсовой с добавленным абзацем в первом параграфе и заменённым первым источником.</summary>
    static CheckReport CheckWith(string? bodyText = null, string? reference = null, Action<Body>? edit = null)
    {
        var p = BuiltInProfiles.Load("kursovaya");
        using var dir = new TempDir();
        var path = TestFiles.Generate(p, dir);
        using (var doc = WordprocessingDocument.Open(path, true))
        {
            var body = doc.MainDocumentPart!.Document!.Body!;
            if (bodyText != null)
            {
                var h2 = body.Descendants<Paragraph>().First(x => x.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "Heading2");
                h2.InsertAfterSelf(Styled("Normal", bodyText));
            }
            if (reference != null)
            {
                var r = body.Descendants<Paragraph>().First(x => x.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "ListReferences");
                r.RemoveAllChildren<Run>();
                r.Append(new Run(new Text(reference)));
            }
            edit?.Invoke(body);
        }
        return DocumentChecker.CheckFile(path, p, p.Name);
    }

    static List<string> Keys(CheckReport r) => r.Issues.Select(i => i.Key).ToList();

    // ---------------- ГОСТ 8.417

    [Theory]
    [InlineData("Масса образца составила 5кг при длине 10 мм и температуре 20 °С.")]
    [InlineData("Частота процессора 3,2ГГц, объём памяти 16 Гбайт, это достаточно.")]
    [InlineData("Расстояние между опорами равно 12м, поэтому выбран другой профиль.")]
    public void ЧислоСлитноСЕдиницей(string text) => Assert.Contains("unitSpace", Keys(CheckWith(text)));

    [Theory]
    [InlineData("Масса образца составила 5 кг, длина 10 мм, температура 20 °С, влажность 45%.")]
    [InlineData("Группа 11А и класс 2В сдали работы в 2024 году, всего 3 варианта.")]
    [InlineData("Использована библиотека версии 2.1, протокол HTTP/2 и стандарт IEEE 754.")]
    public void ПравильныеЕдиницыИЛитерыНеСчитаютсяОшибкой(string text)
    {
        var r = CheckWith(text);
        Assert.DoesNotContain("unitSpace", Keys(r));
        Assert.DoesNotContain("unitSymbol", Keys(r));
    }

    [Theory]
    [InlineData("Время отклика составило 5 сек при нагрузке в сто запросов.")]
    [InlineData("В рецепте указано 200 гр муки и немного соли для теста.")]
    [InlineData("Толщина листа 10 мм. и ширина 200 мм, что соответствует заданию.")]
    public void НеверноеОбозначениеЕдиницы(string text) => Assert.Contains("unitSymbol", Keys(CheckWith(text)));

    [Fact]
    public void КонецПредложенияПослеЕдиницыНеОшибка() =>
        Assert.DoesNotContain("unitSymbol", Keys(CheckWith("Толщина листа составляет 10 мм. Ширина листа выбрана по таблице.")));

    // ---------------- ГОСТ Р 7.0.12, Р 7.0.100

    [Fact]
    public void СтрВместоС()
    {
        var r = CheckWith(reference: "Иванов, И. И. Основы проектирования / И. И. Иванов. – Москва : Наука, 2024. – 256 стр.");
        Assert.Contains("refPages", Keys(r));
        Assert.DoesNotContain("refYear", Keys(r));
    }

    [Fact]
    public void ГодСГ()
    {
        var r = CheckWith(reference: "Иванов, И. И. Основы проектирования / И. И. Иванов. – Москва : Наука, 2024 г. – 256 с.");
        Assert.Contains("refYear", Keys(r));
        Assert.DoesNotContain("refPages", Keys(r));
    }

    [Fact]
    public void ПравильноеОписаниеБезЗамечанийКСокращениям()
    {
        var r = CheckWith(reference: "Иванов, И. И. Основы проектирования / И. И. Иванов. – Москва : Наука, 2024. – 256 с.");
        Assert.DoesNotContain("refPages", Keys(r));
        Assert.DoesNotContain("refYear", Keys(r));
        Assert.Contains("Сокращения в списке источников", r.Passed);
    }

    // ---------------- реферат: ГОСТ 7.32-2017 (5.3.2, 6.12), ГОСТ Р 7.0.99-2018

    /// <summary>Вставляет «РЕФЕРАТ» с абзацами перед «Введением».</summary>
    static Action<Body> Abstract(params string[] lines) => body =>
    {
        var intro = body.Descendants<Paragraph>().First(x => x.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "StructHeading");
        intro.InsertBeforeSelf(Styled("StructHeading", "РЕФЕРАТ"));
        foreach (var l in lines) intro.InsertBeforeSelf(Styled("Normal", l));
    };

    const string Volume = "Курсовая работа 45 с., 6 рис., 3 табл., 20 источн., 1 прил.";
    const string Text = "Объект исследования — процесс оформления студенческих работ. Цель работы — сократить время на оформление.";

    [Fact]
    public void РефератПоГостБезЗамечаний()
    {
        var r = CheckWith(edit: Abstract(Volume, "ШАБЛОН, ГОСТ, ОФОРМЛЕНИЕ, НОРМОКОНТРОЛЬ, КУРСОВАЯ РАБОТА", Text));
        Assert.DoesNotContain(r.Issues, i => i.Category == "Реферат");
        Assert.Contains("Реферат", r.Passed);
    }

    [Fact]
    public void РефератБезОбъёмаИКлючевыхСлов()
    {
        var keys = Keys(CheckWith(edit: Abstract(Text)));
        Assert.Contains("abstractVolume", keys);
        Assert.Contains("abstractKeywords", keys);
    }

    [Theory]
    [InlineData("ШАБЛОН, ГОСТ, ОФОРМЛЕНИЕ")]
    [InlineData("А, Б, В, Г, Д, Е, Ж, И, К, Л, М, Н, П, Р, С, Т")]
    public void ЧислоКлючевыхСловВне5_15(string keywords)
    {
        var r = CheckWith(edit: Abstract(Volume, keywords, Text));
        Assert.Contains(r.Issues, i => i.Key == "abstractKeywords" && i.Title.StartsWith("Ключевых слов"));
    }

    [Fact]
    public void КлючевыеСловаСтрочнымиИСТочкой()
    {
        var r = CheckWith(edit: Abstract(Volume, "Ключевые слова: шаблон, гост, оформление, нормоконтроль, курсовая работа.", Text));
        Assert.Contains("abstractKeywordsStyle", Keys(r));
        Assert.DoesNotContain(r.Issues, i => i.Key == "abstractKeywords");
    }

    [Fact]
    public void БезРефератаПроверкаНеВключается() =>
        Assert.DoesNotContain(CheckWith().Issues, i => i.Category == "Реферат");
}
