using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShablonRabot.Core;

/// <summary>Профиль: правила оформления + титульный лист + структура работы. Формат совпадает с config/*.json.</summary>
public sealed class Profile
{
    public string Name { get; set; } = "";
    public PageRules Page { get; set; } = new();
    public TextRules Text { get; set; } = new();
    public HeadingRules Headings { get; set; } = new();
    public CaptionRules Captions { get; set; } = new();
    public ListRules Lists { get; set; } = new();
    public Dictionary<string, string> Fields { get; set; } = new();
    /// <summary>Подписи и подсказки полей титула для этого вида работы — «ФИО автора» в диссертации и т. п. Необязательно.</summary>
    public Dictionary<string, FieldHint>? FieldHints { get; set; }
    public TitlePage TitlePage { get; set; } = new();
    public List<StructureItem> Structure { get; set; } = new();

    public Profile Clone() => JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(this, ProfileJson.Options), ProfileJson.Options)!;

    /// <summary>Поля титульного листа без служебных ключей (_comment).</summary>
    public IEnumerable<KeyValuePair<string, string>> VisibleFields => Fields.Where(f => !f.Key.StartsWith('_'));
}

public sealed class PageRules
{
    public double WidthMm { get; set; } = 210;
    public double HeightMm { get; set; } = 297;
    public Margins MarginsMm { get; set; } = new();
    public PageNumbering Numbering { get; set; } = new();
}

public sealed class Margins
{
    public double Left { get; set; } = 30;
    public double Right { get; set; } = 15;
    public double Top { get; set; } = 20;
    public double Bottom { get; set; } = 20;
}

public sealed class PageNumbering
{
    public string Align { get; set; } = "center";
    public double SizePt { get; set; } = 12;
    public bool ShowOnTitle { get; set; }
}

public sealed class TextRules
{
    public string Font { get; set; } = "Times New Roman";
    public double SizePt { get; set; } = 14;
    public double LineSpacing { get; set; } = 1.5;
    public double FirstLineIndentCm { get; set; } = 1.25;
    public string Align { get; set; } = "justify";
    public bool Hyphenation { get; set; } = true;
    public string Language { get; set; } = "ru-RU";
}

public sealed class HeadingStyle
{
    /// <summary>indent — с абзацного отступа, center — по центру.</summary>
    public string Align { get; set; } = "indent";
    /// <summary>upper — ПРОПИСНЫЕ, as_is — как написано.</summary>
    public string Case { get; set; } = "as_is";
    public bool Bold { get; set; } = true;
    public double SizePt { get; set; } = 14;
    public bool NewPage { get; set; }
    public double SpaceBeforePt { get; set; }
    public double SpaceAfterPt { get; set; } = 12;
}

public sealed class HeadingRules
{
    public HeadingStyle Structural { get; set; } = new() { Align = "center", Case = "upper", NewPage = true };
    public HeadingStyle Level1 { get; set; } = new() { NewPage = true };
    public HeadingStyle Level2 { get; set; } = new() { SpaceBeforePt = 12 };
    public HeadingStyle Level3 { get; set; } = new() { SpaceBeforePt = 12, SpaceAfterPt = 6 };
    public int TocLevels { get; set; } = 3;

    [JsonIgnore] public HeadingStyle[] Levels => [Level1, Level2, Level3];
}

public sealed class CaptionStyle
{
    public string Prefix { get; set; } = "";
    public string Align { get; set; } = "center";
    public double SizePt { get; set; } = 14;
    public double SpaceAfterPt { get; set; }
}

public sealed class CaptionRules
{
    public string Separator { get; set; } = " – ";
    public CaptionStyle Figure { get; set; } = new() { Prefix = "Рисунок", SpaceAfterPt = 12 };
    public CaptionStyle Table { get; set; } = new() { Prefix = "Таблица", Align = "left" };
    public double TableTextSizePt { get; set; } = 12;
    public bool FormulaWhereIndent { get; set; }
}

public sealed class ListRules
{
    public string Bullet { get; set; } = "–";
}

/// <summary>Переопределение подписи поля титула в профиле. Пустые свойства — как в приложении по умолчанию.</summary>
public sealed class FieldHint
{
    public string? Label { get; set; }
    public string? Placeholder { get; set; }
    public string? Help { get; set; }
    /// <summary>false — не предлагать сокращать ФИО до инициалов (в диссертации пишут полностью).</summary>
    public bool? Initials { get; set; }
}

public sealed class TitlePage
{
    public double FontSizePt { get; set; } = 14;
    public List<TitleBlock> Blocks { get; set; } = new();
    public List<string> BottomLines { get; set; } = new();
}

public sealed class TitleBlock
{
    /// <summary>lines — строки текста, signatures — таблица подписей.</summary>
    public string Type { get; set; } = "lines";
    public string Align { get; set; } = "center";
    public List<string> Lines { get; set; } = new();
    public bool Bold { get; set; }
    /// <summary>Курсив — например, «ДИПЛОМНАЯ РАБОТА» на титуле колледжа.</summary>
    public bool Italic { get; set; }
    public double? SizePt { get; set; }
    public double SpaceBeforePt { get; set; }
    public double IndentLeftCm { get; set; }
    public List<double> ColWidthsCm { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
}

public sealed class StructureItem
{
    /// <summary>toc, structural, chapter, references, appendix.</summary>
    public string Type { get; set; } = "structural";
    public string Title { get; set; } = "";
    public string? Letter { get; set; }
    public List<BodyItem>? Body { get; set; }
    public List<Section>? Sections { get; set; }
    public List<string>? Items { get; set; }
}

public sealed class Section
{
    public string Title { get; set; } = "";
    public List<BodyItem>? Body { get; set; }
}

public enum BodyKind { Text, Dash, Num, H3, Figure, Table, Formula }

[JsonConverter(typeof(BodyItemConverter))]
public sealed class BodyItem
{
    public BodyKind Kind { get; set; }
    public string Text { get; set; } = "";
    public List<string> Items { get; set; } = new();
    public TableSpec? Table { get; set; }
    public FormulaSpec? Formula { get; set; }

    public static BodyItem Paragraph(string text) => new() { Kind = BodyKind.Text, Text = text };
}

public sealed class TableSpec
{
    public string Caption { get; set; } = "";
    public List<string> Header { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
}

public sealed class FormulaSpec
{
    public string Expr { get; set; } = "";
    public List<string> Where { get; set; } = new();
}

/// <summary>Элемент тела раздела: строка — абзац, объект с одним ключом — dash/num/h3/figure/table/formula.</summary>
public sealed class BodyItemConverter : JsonConverter<BodyItem>
{
    public override BodyItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return BodyItem.Paragraph(reader.GetString()!);
        var obj = JsonNode.Parse(ref reader)!.AsObject();
        var (key, value) = obj.First();
        return key switch
        {
            "dash" => new BodyItem { Kind = BodyKind.Dash, Items = value.Deserialize<List<string>>(options)! },
            "num" => new BodyItem { Kind = BodyKind.Num, Items = value.Deserialize<List<string>>(options)! },
            "h3" => new BodyItem { Kind = BodyKind.H3, Text = value!.GetValue<string>() },
            "figure" => new BodyItem { Kind = BodyKind.Figure, Text = value!.GetValue<string>() },
            "table" => new BodyItem { Kind = BodyKind.Table, Table = value.Deserialize<TableSpec>(options) },
            "formula" => new BodyItem { Kind = BodyKind.Formula, Formula = value.Deserialize<FormulaSpec>(options) },
            _ => throw new JsonException($"Неизвестный элемент раздела: {key}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, BodyItem item, JsonSerializerOptions options)
    {
        if (item.Kind == BodyKind.Text)
        {
            writer.WriteStringValue(item.Text);
            return;
        }
        writer.WriteStartObject();
        switch (item.Kind)
        {
            case BodyKind.Dash: writer.WritePropertyName("dash"); JsonSerializer.Serialize(writer, item.Items, options); break;
            case BodyKind.Num: writer.WritePropertyName("num"); JsonSerializer.Serialize(writer, item.Items, options); break;
            case BodyKind.H3: writer.WriteString("h3", item.Text); break;
            case BodyKind.Figure: writer.WriteString("figure", item.Text); break;
            case BodyKind.Table: writer.WritePropertyName("table"); JsonSerializer.Serialize(writer, item.Table, options); break;
            case BodyKind.Formula: writer.WritePropertyName("formula"); JsonSerializer.Serialize(writer, item.Formula, options); break;
        }
        writer.WriteEndObject();
    }
}

public static class ProfileJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Загружает профиль из файла; ключ "extends" подтягивает базовый профиль из той же папки.</summary>
    public static Profile Load(string path) =>
        Deserialize(LoadNode(Path.GetFullPath(path), File.ReadAllText, (from, rel) => Path.Combine(Path.GetDirectoryName(from)!, rel)));

    public static Profile Parse(string json, Func<string, string> resolveBase) =>
        Deserialize(LoadNode("", _ => json, (_, rel) => rel, resolveBase));

    public static string Serialize(Profile profile) => JsonSerializer.Serialize(profile, Options);

    /// <summary>Загрузка профиля, выбранного пользователем, с понятными сообщениями об ошибках.</summary>
    /// <exception cref="ProfileImportException"/>
    public static Profile LoadForImport(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            return Load(path);
        }
        catch (FileNotFoundException e) when (!string.Equals(Path.GetFullPath(e.FileName ?? ""), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileImportException($"Профиль ссылается на файл «{Path.GetFileName(e.FileName)}», но его нет рядом с «{name}». Положите оба файла в одну папку и повторите.");
        }
        catch (MissingSectionException e)
        {
            throw new ProfileImportException($"В файле «{name}» нет раздела «{e.Section}». Попросите кафедру прислать полный профиль.");
        }
        catch (JsonException e)
        {
            var where = e.LineNumber is { } line ? $": ошибка в строке {line + 1}, позиция {(e.BytePositionInLine ?? 0) + 1}" : "";
            throw new ProfileImportException($"Файл «{name}» повреждён или это не профиль{where}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ProfileImportException($"Файл «{name}» не удалось прочитать. Проверьте, что он не открыт в другой программе.");
        }
    }

    public static void Save(Profile profile, string path) => File.WriteAllText(path, Serialize(profile));

    static Profile Deserialize(JsonObject node)
    {
        if (node["structure"] is not JsonArray { Count: > 0 }) throw new MissingSectionException("structure (структура работы)");
        if (node["title_page"] is not JsonObject) throw new MissingSectionException("title_page (титульный лист)");
        // Служебные ключи («_comment») допустимы и в подсказках полей — это не поле
        if (node["field_hints"] is JsonObject hints)
            foreach (var key in hints.Select(p => p.Key).Where(k => k.StartsWith('_')).ToList()) hints.Remove(key);
        return node.Deserialize<Profile>(Options) ?? throw new JsonException("Пустой профиль");
    }

    static JsonObject LoadNode(string path, Func<string, string> read, Func<string, string, string> combine,
                               Func<string, string>? readBase = null)
    {
        var node = JsonNode.Parse(read(path), documentOptions: DocOptions)?.AsObject()
                   ?? throw new JsonException("Файл профиля пуст");
        if (node["extends"] is JsonValue ext)
        {
            node.Remove("extends");
            var basePath = combine(path, ext.GetValue<string>());
            var baseNode = LoadNode(basePath, readBase ?? read, combine, readBase);
            return Merge(baseNode, node);
        }
        return node;
    }

    static JsonObject Merge(JsonObject target, JsonObject over)
    {
        foreach (var (key, value) in over.ToList())
        {
            if (value is JsonObject o && target[key] is JsonObject t)
                Merge(t, o);
            else
                target[key] = value?.DeepClone();
        }
        return target;
    }
}

public sealed class ProfileImportException(string message) : Exception(message);

public sealed class MissingSectionException(string section) : JsonException($"Нет раздела {section}")
{
    public string Section { get; } = section;
}

/// <summary>Профили, встроенные в приложение (Profiles/*.json как ресурсы сборки).</summary>
public static class BuiltInProfiles
{
    public static readonly (string Id, string Title)[] All =
    [
        ("kursovaya", "Курсовая работа"),
        ("vkr", "Выпускная квалификационная работа"),
        ("eskd", "Пояснительная записка (ЕСКД)"),
        ("referat", "Реферат"),
        ("practice", "Отчёт по практике"),
        ("kontrolnaya", "Контрольная работа"),
        ("dissertation", "Диссертация"),
        // Колледжи и техникумы (СПО)
        ("spo-kursovaya", "Курсовая работа (СПО)"),
        ("spo-diplom", "Дипломная работа (СПО)"),
        ("spo-practice", "Отчёт по практике (СПО)"),
        ("spo-project", "Индивидуальный проект (СПО)"),
        ("spo-referat", "Реферат (СПО)"),
    ];

    public static Profile Load(string id) => ProfileJson.Parse(ReadResource($"{id}.json"), ReadResource);

    static string ReadResource(string fileName)
    {
        using var stream = typeof(BuiltInProfiles).Assembly.GetManifestResourceStream($"Profiles.{fileName}")
                           ?? throw new FileNotFoundException($"Встроенный профиль {fileName} не найден");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public static partial class Placeholders
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex FieldRegex();

    /// <summary>Подставляет значения полей в строку вида «Кафедра {department}».</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> fields) =>
        FieldRegex().Replace(template, m => fields.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    /// <summary>Значение считается подсказкой, если оно пустое, в [скобках] или «И. О. Фамилия».</summary>
    public static bool IsPlaceholder(string? value)
    {
        var v = (value ?? "").Trim();
        return v.Length == 0 || (v.StartsWith('[') && v.EndsWith(']')) || v == "И. О. Фамилия";
    }

    public static Dictionary<string, string> Resolve(Profile profile)
    {
        var fields = profile.VisibleFields.ToDictionary(f => f.Key, f => f.Value);
        if (string.IsNullOrWhiteSpace(fields.GetValueOrDefault("year")))
            fields["year"] = DateTime.Today.Year.ToString();
        return fields;
    }
}
