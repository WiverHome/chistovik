using ShablonRabot.Core;

// shablon <профиль.json | kursovaya | vkr | eskd | referat | practice | kontrolnaya | dissertation> -o файл.docx [--pdf] [--no-word]
if (args.Length == 0)
{
    Console.WriteLine("Использование: shablon <профиль.json | kursovaya | vkr | eskd | referat | practice | kontrolnaya | dissertation> -o файл.docx [--pdf] [--no-word]");
    return 1;
}

// shablon check <работа.docx> [--profile kursovaya|профиль.json] [--annotate копия.docx]
if (args[0] == "check")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var pi = Array.IndexOf(args, "--profile");
    var profileId = pi >= 0 ? args[pi + 1] : "kursovaya";
    var rules = BuiltInProfiles.All.Any(p => p.Id == profileId) ? BuiltInProfiles.Load(profileId) : ProfileJson.LoadForImport(profileId);
    var report = ShablonRabot.Core.Checking.DocumentChecker.CheckFile(args[1], rules, rules.Name);
    Console.WriteLine($"{report.FileName} — по правилам «{report.RulesName}»: {report.Summary}");
    foreach (var i in report.Issues)
    {
        Console.WriteLine($"[{i.Severity}] {i.Category}: {i.Title} ({i.Count}) — {i.Detail}");
        foreach (var l in i.Locations.Take(3)) Console.WriteLine($"     • {l.Section} | «{l.Snippet}»");
    }
    Console.WriteLine("Без замечаний: " + string.Join(", ", report.Passed));
    var ai = Array.IndexOf(args, "--annotate");
    if (ai >= 0)
    {
        var n = ShablonRabot.Core.Checking.ReportAnnotator.SaveAnnotated(args[1], report, Path.GetFullPath(args[ai + 1]));
        Console.WriteLine($"Примечаний: {n} → {args[ai + 1]}");
    }
    return 0;
}

// shablon validate <файл.docx> — проверка схемы OpenXML (например, копии с примечаниями)
if (args[0] == "validate")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    using var vdoc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(args[1], false);
    var verrors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(vdoc).ToList();
    foreach (var e in verrors.Take(20)) Console.WriteLine($"XML: {e.Description} @ {e.Path?.XPath}");
    Console.WriteLine($"Ошибок схемы: {verrors.Count}; примечаний: {vdoc.MainDocumentPart?.WordprocessingCommentsPart?.Comments?.ChildElements.Count ?? 0}");
    return 0;
}

// shablon detect <файл> | shablon detect --text "требования"
if (args[0] == "detect")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var detection = args[1] == "--text"
        ? ShablonRabot.Core.Detection.RequirementsParser.Parse(args[2])
        : ShablonRabot.Core.Detection.SampleAnalyzer.AnalyzeFile(args[1]);
    foreach (var r in detection.Rules.OrderBy(r => Array.IndexOf(ShablonRabot.Core.Detection.Rules.Order, r.Key)))
        Console.WriteLine($"{r.Name,-34} {r.Display,-28} {r.Confidence,-6} {r.Evidence}");
    var v = detection.Verdict!;
    Console.WriteLine($"=> {v.Title}. {v.Summary(detection.Rules.Count)}");
    foreach (var d in v.Differences) Console.WriteLine($"   - {d.Name} — {d.Found}, по ГОСТ {d.Required}");
    return 0;
}

var source = args[0];
var profile = BuiltInProfiles.All.Any(p => p.Id == source) ? BuiltInProfiles.Load(source) : ProfileJson.LoadForImport(source);
var outIndex = Array.IndexOf(args, "-o");
var output = outIndex >= 0 ? args[outIndex + 1] : $"{profile.Name}.docx";

var result = TemplateService.Generate(profile, Path.GetFullPath(output), pdf: args.Contains("--pdf"),
    useWord: !args.Contains("--no-word"), progress: new Progress<GenerationStage>(s => Console.WriteLine($"… {s}")));
Console.WriteLine($"Готово: {result.DocxPath}{(result.PdfPath != null ? " + PDF" : "")}");
if (!result.FieldsUpdated) Console.WriteLine($"Поля не обновлены Word ({result.WordIssue}{(result.WordError != null ? ": " + result.WordError : "")}) — обновятся при открытии файла.");

if (args.Contains("--validate"))
{
    using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(result.DocxPath, false);
    var errors = new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(doc).ToList();
    foreach (var e in errors.Take(20)) Console.WriteLine($"XML: {e.Description} @ {e.Path?.XPath}");
    Console.WriteLine($"Ошибок схемы: {errors.Count}");
    return errors.Count == 0 ? 0 : 2;
}
return 0;
