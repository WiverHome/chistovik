using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using ShablonRabot.App.Infrastructure;
using ShablonRabot.Core;
using ShablonRabot.Core.Detection;

namespace ShablonRabot.App.ViewModels;

public enum DetectSource { File, Text }
public enum DetectState { Choose, Analyzing, Result, Error }

/// <summary>Строка найденного правила (DESIGN.md 17.4.5).</summary>
public sealed class DetectedRowViewModel : ObservableObject
{
    bool _isChecked;

    public DetectedRowViewModel(DetectedRule rule, string current, bool same, Action changed)
    {
        Rule = rule;
        CurrentText = same ? "уже так" : $"сейчас: {current}";
        IsSame = same;
        _isChecked = !same && rule.Confidence >= Confidence.Medium;
        Changed = changed;
    }

    public DetectedRule Rule { get; }
    public string Name => Rule.Name;
    public string Value => Rule.Display;
    public string Evidence => Rule.Evidence;
    public string CurrentText { get; }
    public bool IsSame { get; }
    public bool IsLow => Rule.Confidence == Confidence.Low;
    public string ConfidenceText => Rule.Confidence switch { Confidence.High => "Высокая", Confidence.Medium => "Средняя", _ => "Низкая" };
    public string? PartialHint => Rule.Value is MarginsValue { IsComplete: false } m
        ? $"Не найдено: {string.Join(", ", new[] { ("левое", m.Left), ("правое", m.Right), ("верхнее", m.Top), ("нижнее", m.Bottom) }.Where(x => x.Item2 == null).Select(x => x.Item1 + " поле"))}. Оно останется как было."
        : null;
    Action Changed { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (!IsSame && Set(ref _isChecked, value)) Changed(); }
    }

    public string AutomationName =>
        $"{Name}: {Value}. Уверенность {ConfidenceText.ToLowerInvariant()}. {Evidence}. {(IsSame ? "Уже так" : CurrentText)}";
}

public sealed record DetectedGroup(string Title, IReadOnlyList<DetectedRowViewModel> Rows);

/// <summary>Диалог «Определить правила по примеру» (DESIGN.md 17.4).</summary>
public sealed class DetectDialogViewModel : DialogViewModel
{
    static readonly (string Title, string[] Keys)[] Groups =
    [
        ("Страница", ["margins", "pageNumber"]),
        ("Основной текст", ["font", "fontSize", "lineSpacing", "indent", "align", "hyphenation"]),
        ("Заголовки", ["headingAlign", "chapterNewPage", "structuralUpper", "tocLevels"]),
        ("Рисунки и таблицы", ["figurePrefix", "separator", "tableCaptionAlign", "tableFontSize"]),
    ];

    readonly RulesViewModel _rules;
    readonly IDialogService _dialogs;
    readonly Action<DetectDialogViewModel> _apply;
    DetectSource _source;
    DetectState _state = DetectState.Choose;
    string? _filePath;
    string _text = "";
    string? _textError, _stage;
    CancellationTokenSource? _cts;
    RulesDetection? _result;
    DetectSource _resultSource;
    bool _showAllDiffs;

    public DetectDialogViewModel(RulesViewModel rules, IDialogService dialogs, string profileTitle, Action<DetectDialogViewModel> apply)
    {
        _rules = rules;
        _dialogs = dialogs;
        _apply = apply;
        Title = "Определить правила по примеру";
        FooterText = $"Применится к профилю «{profileTitle}»";
        IsWordInstalled = TemplateService.IsWordInstalled;

        ChooseFileCommand = new RelayCommand(ChooseFile, () => State != DetectState.Analyzing);
        AnalyzeTextCommand = new RelayCommand(() => _ = AnalyzeTextAsync(), () => State != DetectState.Analyzing && Text.Trim().Length > 0);
        PasteCommand = new RelayCommand(() => Text = _dialogs.GetClipboardText() ?? Text, () => _dialogs.HasClipboardText());
        BackCommand = new RelayCommand(() => { State = DetectState.Choose; if (Source == DetectSource.Text) FocusText?.Invoke(); });
        RetryCommand = new RelayCommand(() => { if (_filePath != null) _ = AnalyzeFileAsync(_filePath); });
        SwitchToTextCommand = new RelayCommand(() => { Source = DetectSource.Text; State = DetectState.Choose; FocusText?.Invoke(); });
        CancelCommand = new RelayCommand(Dismiss);
        ApplyCommand = new RelayCommand(() => _apply(this), () => State == DetectState.Result && CheckedCount > 0);
        ToggleAllDiffsCommand = new RelayCommand(() => { _showAllDiffs = true; OnPropertyChanged(nameof(Differences)); OnPropertyChanged(nameof(MoreDiffsText)); });
    }

    public static Choice[] Sources { get; } = [new(0, "Пример работы"), new(1, "Текст требований")];
    public bool IsWordInstalled { get; }
    public string FooterText { get; }
    public Action? FocusText { get; set; }
    public Action? Announce { get; set; }

    public int SourceIndex { get => (int)Source; set { if (value >= 0) Source = (DetectSource)value; } }
    public DetectSource Source
    {
        get => _source;
        set
        {
            if (!Set(ref _source, value)) return;
            OnPropertyChanged(nameof(SourceIndex));
            // Результат относится к своей вкладке: на другой — её собственное состояние
            if (State is DetectState.Result or DetectState.Error && _resultSource != value) State = DetectState.Choose;
            else if (_result != null && _resultSource == value && State == DetectState.Choose) State = DetectState.Result;
        }
    }

    public DetectState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            OnPropertyChanged(nameof(IsChoose));
            OnPropertyChanged(nameof(IsAnalyzing));
            OnPropertyChanged(nameof(IsResult));
            OnPropertyChanged(nameof(IsError));
            OnPropertyChanged(nameof(ApplyText));
        }
    }

    public bool IsChoose => State == DetectState.Choose;
    public bool IsAnalyzing => State == DetectState.Analyzing;
    public bool IsResult => State == DetectState.Result;
    public bool IsError => State == DetectState.Error;
    public bool IsFileSource => Source == DetectSource.File;

    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value ?? "")) { TextError = null; System.Windows.Input.CommandManager.InvalidateRequerySuggested(); } }
    }

    public string? TextError { get => _textError; set => Set(ref _textError, value); }
    public string? Stage { get => _stage; set => Set(ref _stage, value); }
    public string FileName => _filePath != null ? Path.GetFileName(_filePath) : "";
    public string FileInfo => _filePath != null && File.Exists(_filePath)
        ? $"{(_filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "Документ")} · {ShareDialogViewModel.FormatSize(new System.IO.FileInfo(_filePath).Length)}" : "";
    public bool IsPdf => _filePath?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true;

    // ------------------------------------------------------------ результат

    public RulesDetection? Result => _result;
    public string SourceLine => _resultSource == DetectSource.File ? FileName : $"Текст требований · {Core.Detection.Rules.Plural(Text.Trim().Length, "символ", "символа", "символов")}";
    public string SourceLink => _resultSource == DetectSource.File ? "Выбрать другой файл" : "Изменить текст";
    public string? SourceWarningTitle => _resultSource != DetectSource.File || _filePath == null ? null
        : IsPdf ? "PDF распознаётся приблизительно." : SampleAnalyzer.NeedsWord(_filePath) ? "Файл открыт через Word." : null;
    public string? SourceWarningText => _resultSource != DetectSource.File || _filePath == null ? null
        : IsPdf ? "Поля и отступы могут отличаться на 1–2 мм, переносы и уровни содержания не определяются. Если есть .docx, загрузите лучше его."
        : SampleAnalyzer.NeedsWord(_filePath) ? "Значения определены по копии в формате .docx — проверьте их перед применением." : null;

    public StandardVerdict? Verdict => _result?.Verdict;
    public string VerdictTitle => Verdict?.Title ?? "";
    public string VerdictText => Verdict?.Summary(_result?.Rules.Count ?? 0) ?? "";
    public string VerdictKind => Verdict?.Kind.ToString() ?? "";
    public IEnumerable<string> Differences => (Verdict is { Kind: Core.Detection.VerdictKind.Matches } v ? v.Differences : [])
        .Take(_showAllDiffs ? int.MaxValue : 4).Select(d => $"{d.Name} — {d.Found}, по ГОСТ {d.Required}");
    public string? MoreDiffsText => Verdict is { Kind: Core.Detection.VerdictKind.Matches } v && v.Differences.Count > 4 && !_showAllDiffs
        ? $"Показать все отличия ({v.Differences.Count})" : null;

    public ObservableCollection<DetectedGroup> ResultGroups { get; } = new();
    public IEnumerable<DetectedRowViewModel> Rows => ResultGroups.SelectMany(g => g.Rows);
    public int CheckedCount => Rows.Count(r => r.IsChecked);
    public int ApplicableCount => Rows.Count(r => !r.IsSame);
    public string ListHeader => $"Применить: {CheckedCount} из {ApplicableCount}";
    public bool? AllChecked
    {
        get => CheckedCount == 0 ? false : CheckedCount == ApplicableCount ? true : null;
        set { var v = value ?? false; foreach (var r in Rows.Where(r => !r.IsSame)) r.IsChecked = v; }
    }
    public string ApplyText => CheckedCount == 0 ? "Применить" : $"Применить {Plural(CheckedCount)}";
    public string? NotFoundText
    {
        get
        {
            if (_result == null) return null;
            var missing = Core.Detection.Rules.Order.Where(k => _result[k] == null).Select(k => Core.Detection.Rules.Names[k]).ToList();
            return missing.Count == 0 ? null : $"Не найдено ({missing.Count}): {string.Join(", ", missing.Select(m => m.ToLowerInvariant()))}. Эти правила останутся как сейчас.";
        }
    }

    // ------------------------------------------------------------ ошибки (17.4.6)

    DetectionError? _error;
    public DetectionError? Error { get => _error; private set { Set(ref _error, value); OnPropertyChanged(string.Empty); } }
    public bool ErrorIsWarning => Error is DetectionError.NeedsWord or DetectionError.Nothing;
    public string ErrorTitle => Error switch
    {
        DetectionError.Unreadable => "Не удалось прочитать файл",
        DetectionError.NeedsWord => "Для этого файла нужен Microsoft Word",
        DetectionError.Locked => "Файл занят",
        DetectionError.Nothing => "Не удалось определить правила",
        DetectionError.WordTimeout => "Word не ответил",
        _ => "",
    };
    public string ErrorText => Error switch
    {
        DetectionError.Unreadable => $"Файл «{FileName}» повреждён, защищён паролем или это не документ. Откройте его в Word, сохраните как .docx и попробуйте снова.",
        DetectionError.NeedsWord => "Файлы .doc, .rtf и .odt открываются через Word, а он не найден. Сохраните файл как .docx в любой программе (например, LibreOffice или Google Документы) или вставьте требования текстом.",
        DetectionError.Locked => $"Файл «{FileName}» открыт в другой программе и недоступен для чтения. Закройте его и нажмите «Повторить».",
        DetectionError.Nothing => $"В файле «{FileName}» почти нет текста — возможно, это скан или набор картинок. Загрузите файл .docx или вставьте требования текстом.",
        DetectionError.WordTimeout => "Word не открыл файл за минуту. Закройте лишние окна Word и нажмите «Повторить» — или сохраните файл как .docx.",
        _ => "",
    };
    public bool CanRetry => Error is DetectionError.Locked or DetectionError.WordTimeout;
    public bool CanChooseOther => Error is DetectionError.Unreadable or DetectionError.NeedsWord or DetectionError.Nothing;
    public bool CanSwitchToText => Error is DetectionError.NeedsWord or DetectionError.Nothing;

    public RelayCommand ChooseFileCommand { get; }
    public RelayCommand AnalyzeTextCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand SwitchToTextCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand ToggleAllDiffsCommand { get; }

    public override void Dismiss()
    {
        if (State == DetectState.Analyzing) { _cts?.Cancel(); return; }
        base.Dismiss();
    }

    // ------------------------------------------------------------ анализ

    void ChooseFile()
    {
        var filter = IsWordInstalled
            ? "Документы (*.docx;*.doc;*.rtf;*.odt;*.pdf)|*.docx;*.doc;*.rtf;*.odt;*.pdf|Все файлы (*.*)|*.*"
            : "Документы (*.docx;*.pdf)|*.docx;*.pdf|Все файлы (*.*)|*.*";
        var path = _dialogs.OpenFile("Выбрать пример работы", filter);
        if (path != null) _ = AnalyzeFileAsync(path);
    }

    public async Task AnalyzeFileAsync(string path)
    {
        Source = DetectSource.File;
        _filePath = path;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FileInfo));
        OnPropertyChanged(nameof(IsPdf));
        var progress = new Progress<DetectionStage>(s => Stage = s switch
        {
            DetectionStage.WordOpening => "Word открывает файл…",
            DetectionStage.Analyzing => "Определяю правила…",
            DetectionStage.Comparing => "Сравниваю с ГОСТ…",
            _ => "Читаю документ…",
        });
        await RunAsync(DetectSource.File, ct => SampleAnalyzer.AnalyzeFile(path, progress, ct));
    }

    public async Task AnalyzeTextAsync()
    {
        var text = Text.Trim();
        if (text.Count(c => !char.IsWhiteSpace(c)) < 20)
        {
            TextError = "Слишком мало текста — вставьте требования целиком, хотя бы одно предложение про поля, шрифт или интервалы.";
            FocusText?.Invoke();
            return;
        }
        Stage = "Определяю правила…";
        await RunAsync(DetectSource.Text, _ => RequirementsParser.Parse(text));
        if (_result is { Rules.Count: 0 } && _resultSource == DetectSource.Text)
        {
            State = DetectState.Choose;
            TextError = "В тексте не нашлось правил оформления. Мы ищем поля, шрифт, кегль, интервал, отступы, выравнивание, заголовки и подписи — проверьте, что вставлены именно требования.";
            FocusText?.Invoke();
        }
    }

    async Task RunAsync(DetectSource source, Func<CancellationToken, RulesDetection> analyze)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var cts = _cts;
        Error = null;
        // «Анализ» показываем, только если он дольше 300 мс — короткий разбор не мигает
        var show = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        show.Tick += (_, _) => { show.Stop(); if (!cts.IsCancellationRequested) State = DetectState.Analyzing; };
        show.Start();
        try
        {
            var result = await Task.Run(() => analyze(cts.Token), cts.Token);
            show.Stop();
            ShowResult(result, source);
        }
        catch (OperationCanceledException)
        {
            show.Stop();
            State = DetectState.Choose;
        }
        catch (DetectionException e)
        {
            show.Stop();
            _resultSource = source;
            Error = e.Kind;
            State = DetectState.Error;
            Announce?.Invoke();
        }
        catch (Exception)
        {
            show.Stop();
            _resultSource = source;
            Error = DetectionError.Unreadable;
            State = DetectState.Error;
        }
    }

    public void ShowResult(RulesDetection result, DetectSource source)
    {
        _result = result;
        _resultSource = source;
        _showAllDiffs = false;
        ResultGroups.Clear();
        void Changed()
        {
            OnPropertyChanged(nameof(CheckedCount));
            OnPropertyChanged(nameof(ListHeader));
            OnPropertyChanged(nameof(AllChecked));
            OnPropertyChanged(nameof(ApplyText));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        foreach (var (title, keys) in Groups)
        {
            var rows = keys.Select(k => result[k]).Where(r => r != null)
                .Select(r => new DetectedRowViewModel(r!, _rules.CurrentDisplay(r!.Key), _rules.IsSame(r!), Changed)).ToList();
            if (rows.Count > 0) ResultGroups.Add(new DetectedGroup(title, rows));
        }
        if (result.Rules.Count > 0) State = DetectState.Result;
        OnPropertyChanged(string.Empty);
        Announce?.Invoke();
    }

    public string AnnounceText => $"Найдено правил: {_result?.Rules.Count ?? 0}. {VerdictTitle}. Отмечено для применения: {CheckedCount}.";

    static string Plural(int n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        var word = mod10 == 1 && mod100 != 11 ? "правило" : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "правила" : "правил";
        return $"{n} {word}";
    }
}
