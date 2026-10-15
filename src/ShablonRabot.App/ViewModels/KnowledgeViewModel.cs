using System.Collections.ObjectModel;
using System.IO;
using ShablonRabot.App.Infrastructure;

namespace ShablonRabot.App.ViewModels;

/// <summary>Статья базы знаний (Knowledge/*.md): заголовок, краткое описание, значок, теги для поиска и текст в Markdown.</summary>
public sealed record KnowledgeArticle(string Id, string Title, string Summary, string Glyph, string Tags, string Markdown);

/// <summary>Раздел «База знаний»: список статей с поиском и выбранная статья.</summary>
public sealed class KnowledgeViewModel : ObservableObject
{
    readonly List<KnowledgeArticle> _all;
    KnowledgeArticle? _selected;
    string _query = "";

    public KnowledgeViewModel()
    {
        _all = Load();
        foreach (var a in _all) Articles.Add(a);
        _selected = _all.FirstOrDefault();
    }

    public ObservableCollection<KnowledgeArticle> Articles { get; } = new();
    public KnowledgeArticle? Selected { get => _selected; set { if (value != null && Set(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); } }
    public bool HasSelection => _selected != null;

    /// <summary>Поиск по заголовку, описанию, тегам и тексту: все слова запроса должны встретиться.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value)) return;
            var words = value.ToLowerInvariant().Replace('ё', 'е').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Articles.Clear();
            foreach (var a in _all)
            {
                var hay = $"{a.Title} {a.Summary} {a.Tags} {a.Markdown}".ToLowerInvariant().Replace('ё', 'е');
                if (words.All(hay.Contains)) Articles.Add(a);
            }
            OnPropertyChanged(nameof(NothingFound));
            if (Articles.Count > 0 && (_selected == null || !Articles.Contains(_selected))) Selected = Articles[0];
        }
    }
    public bool NothingFound => Articles.Count == 0;

    public void Open(string id)
    {
        if (_all.FirstOrDefault(a => a.Id == id) is not { } article) return;
        if (!Articles.Contains(article)) Query = "";
        Selected = article;
    }

    static List<KnowledgeArticle> Load()
    {
        var asm = typeof(KnowledgeViewModel).Assembly;
        var result = new List<KnowledgeArticle>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Knowledge.") && n.EndsWith(".md")).Order())
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            // Лицензия: в конце — тексты лицензий сторонних компонентов, заголовки на уровень ниже
            if (name == "Knowledge.99-license.md" && asm.GetManifestResourceStream("Notices.md") is { } notices)
                using (var n = new StreamReader(notices))
                    text += "\n" + string.Join("\n", n.ReadToEnd().Replace("\r\n", "\n").Split('\n').Select(l => l.StartsWith('#') ? "#" + l : l));
            // Метаданные можно писать и в HTML-комментариях («<!-- @summary … -->») — так их не видно на GitHub
            var lines = text.Replace("\r\n", "\n").Split('\n')
                .Select(l => l.StartsWith("<!-- @") && l.TrimEnd().EndsWith("-->") ? l.TrimEnd()[5..^3].Trim() : l).ToList();
            string Meta(string key) => lines.FirstOrDefault(l => l.StartsWith($"@{key} "))?[(key.Length + 2)..].Trim() ?? "";
            var title = lines.FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim() ?? name;
            var glyph = Meta("glyph");
            var body = string.Join("\n", lines.Where(l => !l.StartsWith('@')));
            // Id — имя файла без номера: «07-references.md» → «references»
            var file = name["Knowledge.".Length..^".md".Length];
            var id = file.Contains('-') && char.IsDigit(file[0]) ? file[(file.IndexOf('-') + 1)..] : file;
            result.Add(new KnowledgeArticle(id, title, Meta("summary"),
                glyph.Length > 0 ? ((char)Convert.ToInt32(glyph, 16)).ToString() : "", Meta("tags"), body));
        }
        return result;
    }
}
