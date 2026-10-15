using System.Collections.ObjectModel;
using System.IO;
using ShablonRabot.App.Infrastructure;
using ShablonRabot.Core;
using ShablonRabot.Core.Checking;
using ShablonRabot.Core.Detection;

namespace ShablonRabot.App.ViewModels;

public enum CheckState { Empty, Checking, Result, Error }

/// <summary>Одно замечание в отчёте: раскрывается — что не так, как исправить, где найдено.</summary>
public sealed class IssueViewModel(CheckIssue issue) : ObservableObject
{
    bool _expanded;

    public CheckIssue Issue { get; } = issue;
    public IssueSeverity Severity => Issue.Severity;
    public string SeverityText => Issue.Severity switch { IssueSeverity.Error => "Ошибка", IssueSeverity.Warning => "Замечание", _ => "Совет" };
    public string Title => Issue.Title;
    public string Category => Issue.Category;
    public string Detail => Issue.Detail;
    public string? Fix => Issue.Fix;
    public string? Source => Issue.Source is { } s ? $"Основание: {s}" : null;
    public string CountText => Issue.Count > 1 ? Rules.Plural(Issue.Count, "место", "места", "мест") : "";
    public IEnumerable<IssueLocation> Locations => Issue.Locations.Take(8);
    public string? MoreText => Issue.Count > 8 ? $"и ещё {Rules.Plural(Issue.Count - 8, "место", "места", "мест")} — все отмечены в копии с примечаниями" : null;
    public bool HasLocations => Issue.Locations.Count > 0;
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public string AutomationName => $"{SeverityText}: {Title}" + (Issue.Count > 1 ? $", {CountText}" : "");
}

/// <summary>Раздел «Проверка работы»: готовая работа (.docx) сверяется с правилами выбранного вида работы.</summary>
public sealed class CheckViewModel : ObservableObject
{
    readonly IDialogService _dialogs;
    readonly Func<(Profile Rules, string Name, string? Gost)> _rules;
    readonly Func<string?> _lastDirectory;
    CheckState _state;
    string? _path, _docx;
    bool _docxIsTemp;
    CheckReport? _report;
    string? _verdict, _errorTitle, _errorText, _savedPath;
    int _filter;
    CancellationTokenSource? _cts;

    public CheckViewModel(IDialogService dialogs, Func<(Profile, string, string?)> rules, Func<string?> lastDirectory)
    {
        _dialogs = dialogs;
        _rules = rules;
        _lastDirectory = lastDirectory;
        ChooseFileCommand = new RelayCommand(ChooseFile, () => State != CheckState.Checking);
        CheckAgainCommand = new RelayCommand(() => { if (_path != null) _ = CheckAsync(_path); }, () => _path != null && State != CheckState.Checking);
        SaveAnnotatedCommand = new RelayCommand(SaveAnnotated, () => State == CheckState.Result && _report is { Issues.Count: > 0 } && _docx != null);
        OpenSavedCommand = new RelayCommand(() => { if (_savedPath != null) _dialogs.OpenPath(_savedPath); }, () => _savedPath != null);
        ShowSavedCommand = new RelayCommand(() => { if (_savedPath != null) _dialogs.ShowInFolder(_savedPath); }, () => _savedPath != null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => State == CheckState.Checking);
        ResetCommand = new RelayCommand(() => { Cleanup(); _path = null; State = CheckState.Empty; OnPropertyChanged(string.Empty); });
        ToggleCommand = new RelayCommand(p => { if (p is IssueViewModel i) i.IsExpanded = !i.IsExpanded; });
    }

    public RelayCommand ChooseFileCommand { get; }
    public RelayCommand CheckAgainCommand { get; }
    public RelayCommand SaveAnnotatedCommand { get; }
    public RelayCommand OpenSavedCommand { get; }
    public RelayCommand ShowSavedCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ToggleCommand { get; }

    /// <summary>Озвучить итог проверки для экранного диктора.</summary>
    public Action? Announce { get; set; }
    /// <summary>Сообщение в инфобаре главного окна (сохранение копии).</summary>
    public Action<string, string?>? Saved { get; set; }

    public CheckState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(IsChecking)); OnPropertyChanged(nameof(IsResult)); OnPropertyChanged(nameof(IsError));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsEmpty => State == CheckState.Empty;
    public bool IsChecking => State == CheckState.Checking;
    public bool IsResult => State == CheckState.Result;
    public bool IsError => State == CheckState.Error;

    /// <summary>По каким правилам будет проверка — правила выбранного вида работы, с изменениями пользователя.</summary>
    public string RulesText
    {
        get
        {
            var (_, name, gost) = _rules();
            return gost != null ? $"По правилам вида работы «{name}» ({gost}) — с вашими изменениями на странице «Правила оформления»."
                                : $"По правилам профиля «{name}» — с вашими изменениями на странице «Правила оформления».";
        }
    }
    public void RefreshRules() => OnPropertyChanged(nameof(RulesText));

    public string FileName => _path != null ? Path.GetFileName(_path) : "";
    public string Summary => _report?.Summary ?? "";
    public string? Verdict => _verdict;
    public int Errors => _report?.Errors ?? 0;
    public int Warnings => _report?.Warnings ?? 0;
    public int Advices => _report?.Advices ?? 0;
    public bool IsClean => _report is { Issues.Count: 0 };
    public string RulesUsed => _report != null ? $"Проверено по правилам «{_report.RulesName}» · абзацев основного текста: {_report.BodyParagraphCount}" : "";
    public string PassedText => _report is { Passed.Count: > 0 } r ? "Без замечаний: " + string.Join(", ", r.Passed.Select((x, i) => i == 0 ? x.FirstLower() : x.FirstLower())) + "." : "";
    public string? ErrorTitle => _errorTitle;
    public string? ErrorText => _errorText;
    public string? SavedPath => _savedPath;
    public string? SavedText => _savedPath != null ? $"Копия с примечаниями: {Path.GetFileName(_savedPath)}" : null;

    public static Choice[] Filters { get; } = [new(0, "Все"), new(1, "Ошибки"), new(2, "Замечания"), new(3, "Советы")];
    public int Filter { get => _filter; set { if (Set(ref _filter, value)) Rebuild(); } }

    public ObservableCollection<IssueViewModel> Issues { get; } = new();
    public string FilterEmptyText => Filter switch { 1 => "Ошибок нет.", 2 => "Замечаний нет.", 3 => "Советов нет.", _ => "" };
    public bool IsFilterEmpty => IsResult && !IsClean && Issues.Count == 0;

    void Rebuild()
    {
        Issues.Clear();
        if (_report == null) return;
        foreach (var i in _report.Issues.Where(i => Filter switch
                 {
                     1 => i.Severity == IssueSeverity.Error, 2 => i.Severity == IssueSeverity.Warning, 3 => i.Severity == IssueSeverity.Advice, _ => true,
                 }))
            Issues.Add(new IssueViewModel(i));
        if (Issues.Count > 0 && Issues.All(i => !i.IsExpanded)) Issues[0].IsExpanded = true;
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(FilterEmptyText));
    }

    void ChooseFile()
    {
        var path = _dialogs.OpenFile("Готовая работа для проверки",
            "Документы Word (*.docx;*.doc;*.rtf;*.odt)|*.docx;*.docm;*.doc;*.rtf;*.odt|Все файлы (*.*)|*.*");
        if (path != null) _ = CheckAsync(path);
    }

    public bool CanCheck(string path) => SampleAnalyzer.IsSupported(path) && !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task CheckAsync(string path)
    {
        if (State == CheckState.Checking) return;
        Cleanup();
        _path = path;
        _report = null;
        _verdict = null;
        _savedPath = null;
        OnPropertyChanged(string.Empty);
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("PDF проверить нельзя",
                "В PDF не видно настроек Word: стилей, полей, интервалов. Проверьте исходный файл .docx — из него и сделан PDF.");
            return;
        }
        State = CheckState.Checking;
        var cts = _cts = new CancellationTokenSource();
        var (rules, name, gost) = _rules();
        var rulesName = gost != null ? $"{name} ({gost})" : name;
        try
        {
            var (report, docx, isTemp, verdict) = await Task.Run(() =>
            {
                var (d, temp) = DocumentChecker.PrepareDocx(path, cts.Token);
                var r = DocumentChecker.CheckDocx(d, rules, rulesName, Path.GetFileName(path));
                // Заодно — на какой ГОСТ похоже оформление (подсказка, если вид работы выбран не тот)
                string? v = null;
                try
                {
                    var det = DocxAnalyzer.Analyze(d, Path.GetFileName(path));
                    if (det.Verdict is { Kind: VerdictKind.Matches } m) v = m.Title + (m.Standard!.IsOutdated ? $": стандарт заменён — {m.Standard.ReplacedBy}" : "");
                }
                catch (Exception) { /* подсказка необязательна */ }
                return (r, d, temp, v);
            }, cts.Token);
            _report = report;
            _docx = docx;
            _docxIsTemp = isTemp;
            _verdict = verdict != null ? $"Оформление работы {verdict.FirstLower()}." : null;
            Filter = 0;
            Rebuild();
            State = CheckState.Result;
            OnPropertyChanged(string.Empty);
            Announce?.Invoke();
        }
        catch (OperationCanceledException)
        {
            State = _report != null ? CheckState.Result : CheckState.Empty;
        }
        catch (DetectionException e)
        {
            ShowError(e.Kind switch
            {
                DetectionError.NeedsWord => "Для этого файла нужен Microsoft Word",
                DetectionError.Locked => "Файл занят",
                DetectionError.WordTimeout => "Word не ответил",
                _ => "Не удалось прочитать файл",
            }, e.Kind switch
            {
                DetectionError.NeedsWord => "Файлы .doc, .rtf и .odt открываются через Word, а он не найден. Сохраните работу как .docx и проверьте снова.",
                DetectionError.Locked => $"Файл «{e.FileName}» открыт в другой программе. Закройте его и нажмите «Проверить заново».",
                DetectionError.WordTimeout => "Word не открыл файл за минуту. Закройте лишние окна Word или сохраните работу как .docx.",
                _ => $"Файл «{e.FileName}» повреждён, защищён паролем или это не документ Word. Откройте его в Word, сохраните как .docx и попробуйте снова.",
            });
        }
        finally
        {
            if (_cts == cts) _cts = null;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    void ShowError(string title, string text)
    {
        _errorTitle = title;
        _errorText = text;
        State = CheckState.Error;
        OnPropertyChanged(nameof(ErrorTitle));
        OnPropertyChanged(nameof(ErrorText));
        Announce?.Invoke();
    }

    public string AnnounceText => State == CheckState.Error ? $"{ErrorTitle}. {ErrorText}" : $"Проверка завершена. {Summary}";

    void SaveAnnotated()
    {
        if (_report == null || _docx == null || _path == null) return;
        var name = Path.GetFileNameWithoutExtension(_path) + " — замечания.docx";
        var target = _dialogs.SaveFile("Сохранить копию с примечаниями", "Документ Word (*.docx)|*.docx", name,
            Path.GetDirectoryName(_path) ?? _lastDirectory());
        if (target == null) return;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Нельзя перезаписать проверяемую работу", "Сохраните копию под другим именем — исходный файл должен остаться без изменений.");
            return;
        }
        try
        {
            var count = ReportAnnotator.SaveAnnotated(_docx, _report, target);
            _savedPath = target;
            OnPropertyChanged(nameof(SavedPath));
            OnPropertyChanged(nameof(SavedText));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            Saved?.Invoke($"Сохранена копия с примечаниями: {Rules.Plural(count, "примечание", "примечания", "примечаний")}. Откройте её в Word: Рецензирование → Примечания.", target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError("Не удалось сохранить копию", $"Файл «{Path.GetFileName(target)}» открыт в другой программе или папка недоступна для записи. Закройте файл или выберите другую папку.");
        }
    }

    public void Cleanup()
    {
        if (_docxIsTemp && _docx != null) try { File.Delete(_docx); } catch (IOException) { }
        _docx = null;
        _docxIsTemp = false;
    }
}

static class StringExtensions
{
    public static string FirstUpper(this string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    /// <summary>Первая буква строчная, если второе слово не аббревиатура («Поля страницы» → «поля страницы», «ГОСТ» не трогаем).</summary>
    public static string FirstLower(this string s) => s.Length < 2 || char.IsUpper(s[1]) ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
