using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ShablonRabot.Core.Checking;

/// <summary>
/// Копия работы с примечаниями Word (Рецензирование → Примечания) у абзацев, где найдены замечания.
/// Исходный файл не меняется; в копии меняются только примечания.
/// </summary>
public static class ReportAnnotator
{
    public const string Author = "Чистовик";
    const int MaxComments = 300;

    /// <summary>Возвращает число добавленных примечаний.</summary>
    public static int SaveAnnotated(string sourceDocx, CheckReport report, string targetDocx)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetDocx))!;
        var temp = Path.Combine(dir, $"~{Guid.NewGuid():N}.docx");
        File.Copy(sourceDocx, temp, overwrite: true);
        try
        {
            int added;
            using (var doc = WordprocessingDocument.Open(temp, true))
            {
                var main = doc.MainDocumentPart ?? throw new InvalidDataException("В файле нет основного документа.");
                var paragraphs = main.Document!.Body!.Descendants<Paragraph>().ToList();
                var commentsPart = main.WordprocessingCommentsPart ?? main.AddNewPart<WordprocessingCommentsPart>();
                commentsPart.Comments ??= new Comments();
                var nextId = commentsPart.Comments.Elements<Comment>()
                    .Select(c => int.TryParse(c.Id?.Value, out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1;

                // Одно примечание на абзац: несколько замечаний к нему — списком
                var byParagraph = new SortedDictionary<int, List<string>>();
                foreach (var issue in report.Issues)
                    foreach (var loc in issue.Locations)
                    {
                        if (loc.ParagraphIndex < 0 || loc.ParagraphIndex >= paragraphs.Count) continue;
                        if (!byParagraph.TryGetValue(loc.ParagraphIndex, out var list)) byParagraph[loc.ParagraphIndex] = list = [];
                        var text = $"{Mark(issue.Severity)} {issue.Title}. {issue.Detail}" + (issue.Fix != null ? $" Как исправить: {issue.Fix}" : "");
                        if (!list.Contains(text)) list.Add(text);
                    }

                added = 0;
                foreach (var (index, texts) in byParagraph)
                {
                    if (added >= MaxComments) break;
                    var id = (nextId++).ToString();
                    var comment = new Comment { Id = id, Author = Author, Initials = "Ч", Date = DateTime.Now };
                    foreach (var t in texts) comment.Append(new Paragraph(new Run(new Text(t) { Space = SpaceProcessingModeValues.Preserve })));
                    commentsPart.Comments.Append(comment);
                    Attach(paragraphs[index], id);
                    added++;
                }
                commentsPart.Comments.Save();
                main.Document.Save();
            }
            File.Move(temp, targetDocx, overwrite: true);
            return added;
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch (IOException) { }
        }
    }

    static string Mark(IssueSeverity s) => s switch { IssueSeverity.Error => "[Ошибка]", IssueSeverity.Warning => "[Замечание]", _ => "[Совет]" };

    /// <summary>Отмечает весь абзац: начало и конец диапазона примечания, затем ссылка на него.</summary>
    static void Attach(Paragraph p, string id)
    {
        var start = new CommentRangeStart { Id = id };
        if (p.ParagraphProperties != null) p.ParagraphProperties.InsertAfterSelf(start);
        else p.PrependChild(start);
        p.Append(new CommentRangeEnd { Id = id });
        p.Append(new Run(new CommentReference { Id = id }));
    }
}
