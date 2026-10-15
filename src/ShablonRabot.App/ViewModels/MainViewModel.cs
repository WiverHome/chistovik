using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using ShablonRabot.App.Infrastructure;
using ShablonRabot.App.Services;
using ShablonRabot.Core;
using ShablonRabot.Core.Detection;

namespace ShablonRabot.App.ViewModels;

public enum AppPage { Type, TitlePage, Rules, Structure, Settings }

public interface IDialogService
{
    string? SaveFile(string title, string filter, string fileName, string? directory);
    string? OpenFile(string title, string filter);
    string? PickFolder(string title, string? directory);
    void OpenPath(string path);
    void ShowInFolder(string path);
    void CopyFileToClipboard(string path);
    void CopyText(string text);
    string? GetClipboardText();
    bool HasClipboardText();
}

public sealed class NavItemViewModel(AppPage page, string title, string glyph, string shortcut) : ObservableObject
{
    string _subtitle = "";
    int _errors;
    bool _modified;

    public AppPage AppPage { get; } = page;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Shortcut { get; } = shortcut;
    public string Subtitle { get => _subtitle; set { if (Set(ref _subtitle, value)) { OnPropertyChanged(nameof(ToolTip)); OnPropertyChanged(nameof(AutomationName)); } } }
    public int ErrorCount { get => _errors; set { if (Set(ref _errors, value)) OnPropertyChanged(nameof(HasErrors)); } }
    public bool HasErrors => ErrorCount > 0;
    public bool IsModified { get => _modified; set => Set(ref _modified, value); }
    public bool IsSingleLine => string.IsNullOrEmpty(Subtitle);
    public string ToolTip => string.IsNullOrEmpty(Subtitle) ? $"{Title} ({Shortcut})" : $"{Title} — {Subtitle.ToLowerInvariant()} ({Shortcut})";
    public string AutomationName => string.IsNullOrEmpty(Subtitle) ? Title : $"{Title}, {Subtitle}";
}

/// <summary>Карточка-кнопка импорта в сетке профилей кафедр.</summary>
public sealed class ImportTile
{
    public static readonly ImportTile Instance = new();
}

public sealed class ProfileCard : ObservableObject
{
    bool _selected;

    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public bool IsSoon { get; init; }
    public bool IsImported { get; init; }
    public bool IsBroken { get; init; }
    public ImportedProfile? Imported { get; init; }
    public string NavTitle { get; init; } = "";
    /// <summary>Стандарт, на котором основаны правила встроенного профиля.</summary>
    public string Gost { get; init; } = "ГОСТ 7.32-2017";
    public bool IsSelected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(AutomationName)); OnPropertyChanged(nameof(IsChecked)); } }
    }
    /// <summary>Для RadioButton (TwoWay): выбор с клавиатуры, мышью и через UI Automation переключает профиль.</summary>
    public bool IsChecked
    {
        get => _selected;
        set
        {
            if (value && !_selected && IsSelectable) SelectRequested?.Invoke(this);
            OnPropertyChanged();
        }
    }
    public Action<ProfileCard>? SelectRequested { get; set; }
    public bool IsEnabled => !IsSoon;
    public bool IsSelectable => !IsSoon && !IsBroken;
    public string AutomationName => IsSelected ? $"{Title}, выбрано" : Title;
    public string MenuName => $"Действия с профилем «{Title}»";
}

public sealed class MainViewModel : ObservableObject
{
    const string DocxFilter = "Документ Word (*.docx)|*.docx";
    const string ProfileFilter = "Профиль оформления (*.json)|*.json";
    public const string Version = "1.0.0";

    readonly AppSettings _settings;
    readonly IDialogService _dialogs;
    readonly DispatcherTimer _autosave, _typing;
    Profile _original = new();
    ProfileCard? _profile;
    AppPage _page;
    PreviewMode? _manualPreview;
    InfoBarViewModel? _infoBar;
    DialogViewModel? _dialog;
    Action? _undo;
    bool _switching, _isStructureModified, _savePdf, _openAfter, _isBusy;
    CancellationTokenSource? _cts;
    (RulesDetection Result, DetectSource Source, string? File, string Text)? _lastDetect;

    public MainViewModel(AppSettings settings, IDialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _savePdf = settings.SavePdf;
        _openAfter = settings.OpenAfter;
        IsWordInstalled = TemplateService.IsWordInstalled;
        if (!IsWordInstalled) _savePdf = false;
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autosave.Tick += (_, _) => { _autosave.Stop(); SaveSession(); };

        Nav =
        [
            new NavItemViewModel(AppPage.Type, "Вид работы", "", "Ctrl+1"),
            new NavItemViewModel(AppPage.TitlePage, "Титульный лист", "", "Ctrl+2"),
            new NavItemViewModel(AppPage.Rules, "Правила оформления", "", "Ctrl+3"),
            new NavItemViewModel(AppPage.Structure, "Структура работы", "", "Ctrl+4"),
        ];
        SettingsNav = new NavItemViewModel(AppPage.Settings, "Параметры", "", "Ctrl+,");

        Fields.Changed += (_, _) => OnEdited(clearUndo: true);
        Rules.Changed += (_, _) => OnEdited(clearUndo: true);
        _typing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _typing.Tick += (_, _) => { _typing.Stop(); OnEdited(clearUndo: true); };
        Rules.TypingChanged += (_, _) => { _typing.Stop(); _typing.Start(); UpdateNav(); };
        Structure.Changed += (_, _) => OnEdited(clearUndo: true);
        Structure.Deleted += (text, undo) => ShowUndo(text, undo);
        Structure.Limited += text => ShowInfo(InfoKind.Warning, null, text);
        Rules.Reverted += key => FocusRequested?.Invoke(AppPage.Rules, "rule:" + key);

        NavigateCommand = new RelayCommand(p => Navigate(p), _ => Dialog == null);
        SelectProfileCommand = new RelayCommand(p => { if (p is ProfileCard { IsSelectable: true } c) SelectProfile(c); });
        ImportCommand = new RelayCommand(() => ImportInteractive(), () => Dialog == null);
        RemoveProfileCommand = new RelayCommand(async p => await RemoveProfileAsync(p as ProfileCard));
        ShowProfileFileCommand = new RelayCommand(p => { if (p is ProfileCard { Imported: { } i }) _dialogs.ShowInFolder(i.Path); });
        GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => !IsBusy && Dialog == null);
        ResetRulesCommand = new RelayCommand(p => ResetRules(p as string ?? "profile"), _ => Rules.ChangedCount > 0 || !Rules.BaselineIsGost);
        ResetStructureCommand = new RelayCommand(ResetStructure, () => IsStructureModified);
        ClearFieldsCommand = new RelayCommand(async () => await ClearFieldsAsync(), () => Fields.HasAnyValue);
        SaveProfileCommand = new RelayCommand(SaveProfileDialog, () => Dialog == null);
        ShareCommand = new RelayCommand(ShareDialog, () => Dialog == null);
        UndoCommand = new RelayCommand(Undo, () => _undo != null);
        OpenLastCommand = new RelayCommand(() => { if (LastFileExists) _dialogs.OpenPath(_settings.LastFile!); });
        ShowLastCommand = new RelayCommand(() => { if (LastFileExists) _dialogs.ShowInFolder(_settings.LastFile!); });
        ZoomCommand = new RelayCommand(() => ShowDialog(new ZoomDialogViewModel(this)), () => Dialog == null);
        DismissWelcomeCommand = new RelayCommand(() => { _settings.FirstRunDone = true; _settings.Save(); OnPropertyChanged(nameof(ShowWelcome)); });
        ChangeFolderCommand = new RelayCommand(ChangeDefaultFolder);
        ClearDataCommand = new RelayCommand(async () => await ClearDataAsync());
        OpenProfilesFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(AppSettings.ProfilesDir); _dialogs.OpenPath(AppSettings.ProfilesDir); });
        NextCommand = new RelayCommand(() => Navigate(_page + 1));
        DetectCommand = new RelayCommand(p => OpenDetect(p as string == "text" ? DetectSource.Text : DetectSource.File), _ => Dialog == null && !IsBusy);
        DetectFromClipboardCommand = new RelayCommand(OpenDetectFromClipboard, () => Dialog == null && !IsBusy);
        ShowDetectResultCommand = new RelayCommand(ShowLastDetectResult, () => _lastDetect != null && Dialog == null);

        BuildCards();
        var start = Cards.FirstOrDefault(c => c.Key == settings.LastProfile && c.IsSelectable) ?? Cards[0];
        SelectProfile(start, initial: true);
        _page = !settings.FirstRunDone ? AppPage.Type
            : Enum.TryParse<AppPage>(settings.LastPage, out var last) && last != AppPage.Settings ? last : AppPage.TitlePage;
        Preview.Mode = AutoPreviewMode(_page);
    }

    // ------------------------------------------------------------ свойства

    public IReadOnlyList<NavItemViewModel> Nav { get; }
    public NavItemViewModel SettingsNav { get; }
    public ObservableCollection<ProfileCard> Cards { get; } = new();
    public IEnumerable<ProfileCard> BuiltInCards => Cards.Where(c => !c.IsImported);
    public IEnumerable<ProfileCard> ImportedCards => Cards.Where(c => c.IsImported);
    /// <summary>Профили кафедр + карточка «Импортировать профиль…» в одной сетке.</summary>
    public IEnumerable<object> ImportedTiles => ImportedCards.Cast<object>().Append(ImportTile.Instance);
    public FieldsViewModel Fields { get; } = new();
    public RulesViewModel Rules { get; } = new();
    public StructureViewModel Structure { get; } = new();
    public PreviewViewModel Preview { get; } = new();
    public bool IsWordInstalled { get; }

    /// <summary>«Правила определены по примеру «…» · Похоже на ГОСТ …» (DESIGN.md 17.8).</summary>
    public string? DetectedSourceText => _profile != null && _settings.For(_profile.Key).DetectedSource is { } src
        ? $"Правила определены по примеру «{src}»" + (_settings.For(_profile.Key).DetectedVerdict is { } v ? $" · {v}" : "") : null;
    public bool HasDetectedSource => DetectedSourceText != null;
    public bool CanShowDetectResult => _lastDetect != null;
    public bool ShowWelcome => !_settings.FirstRunDone && CurrentPage == AppPage.Type;
    public ProfileCard? SelectedProfile => _profile;
    public string ProfileTitle => _profile?.Title ?? "";
    public bool IsImportedProfile => _profile?.IsImported == true;
    public string ResetToProfileText => $"К правилам профиля «{ProfileTitle}»";
    public string ResetButtonAccess => IsImportedProfile ? "_Сбросить" : "Сбросить к _ГОСТ";
    public string ResetButtonText => IsImportedProfile ? "Сбросить" : "Сбросить к ГОСТ";
    public bool CanResetRules => Rules.ChangedCount > 0 || Rules.HasErrors || IsImportedProfile;

    public AppPage CurrentPage
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            _manualPreview = null;
            Preview.Mode = AutoPreviewMode(value);
            OnPropertyChanged(nameof(PreviewModeIndex));
            OnPropertyChanged(nameof(SelectedNav));
            OnPropertyChanged(nameof(ShowWelcome));
            ClearUndo();
            if (InfoBar is { IsUndo: false, IsValidation: false }) InfoBar = null;
            // Первый запуск завершён, когда пользователь ушёл со страницы «Вид работы»
            if (value != AppPage.Type) _settings.FirstRunDone = true;
            if (value != AppPage.Settings) _settings.LastPage = value.ToString();
            _settings.Save();
        }
    }

    /// <summary>Выбор в навигации (ListBox). Параметры — отдельный пункт внизу.</summary>
    public NavItemViewModel? SelectedNav
    {
        get => Nav.FirstOrDefault(n => n.AppPage == _page);
        set { if (value != null) CurrentPage = value.AppPage; }
    }

    public int PreviewModeIndex
    {
        get => (int)Preview.Mode;
        set
        {
            if (value < 0) return;
            _manualPreview = (PreviewMode)value;
            Preview.Mode = (PreviewMode)value;
            OnPropertyChanged();
        }
    }

    public InfoBarViewModel? InfoBar { get => _infoBar; private set => Set(ref _infoBar, value); }
    public DialogViewModel? Dialog { get => _dialog; private set { if (Set(ref _dialog, value)) OnPropertyChanged(nameof(HasDialog)); } }
    public bool HasDialog => Dialog != null;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool IsStructureModified { get => _isStructureModified; private set => Set(ref _isStructureModified, value); }

    public bool SavePdf { get => _savePdf; set { if (Set(ref _savePdf, value)) { _settings.SavePdf = value; _settings.Save(); } } }
    public bool OpenAfter { get => _openAfter; set { if (Set(ref _openAfter, value)) { _settings.OpenAfter = value; _settings.Save(); } } }

    public bool LastFileExists => _settings.LastFile != null && File.Exists(_settings.LastFile);
    public string LastFileName => _settings.LastFile != null ? PathText.Middle(Path.GetFileName(_settings.LastFile), 40) : "";
    public string? LastFilePath => _settings.LastFile;

    public Choice[] ThemeChoices { get; } = [new("system", "Как в Windows"), new("light", "Светлая"), new("dark", "Тёмная")];
    public string Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;
            _settings.Theme = value;
            _settings.Save();
            ThemeService.Apply(value);
            OnPropertyChanged();
        }
    }
    public string DefaultFolder => _settings.DefaultDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string AboutText => $"«Чистовик» — шаблоны студенческих работ по ГОСТ. Версия {Version} · ГОСТ 7.32-2017, ГОСТ Р 2.105-2019, ГОСТ Р 7.0.11-2011";

    /// <summary>Запрос фокуса в представлении: ключ поля титула, имя правила или «error».</summary>
    public event Action<AppPage, string>? FocusRequested;

    public RelayCommand NavigateCommand { get; }
    public RelayCommand SelectProfileCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand RemoveProfileCommand { get; }
    public RelayCommand ShowProfileFileCommand { get; }
    public RelayCommand GenerateCommand { get; }
    public RelayCommand ResetRulesCommand { get; }
    public RelayCommand ResetStructureCommand { get; }
    public RelayCommand ClearFieldsCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand ShareCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand OpenLastCommand { get; }
    public RelayCommand ShowLastCommand { get; }
    public RelayCommand ZoomCommand { get; }
    public RelayCommand DismissWelcomeCommand { get; }
    public RelayCommand ChangeFolderCommand { get; }
    public RelayCommand ClearDataCommand { get; }
    public RelayCommand OpenProfilesFolderCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand DetectCommand { get; }
    public RelayCommand DetectFromClipboardCommand { get; }
    public RelayCommand ShowDetectResultCommand { get; }

    // ------------------------------------------------------------ навигация

    static PreviewMode AutoPreviewMode(AppPage page) => page switch
    {
        AppPage.Rules => PreviewMode.Page,
        AppPage.Structure => PreviewMode.Toc,
        _ => PreviewMode.Title,
    };

    void Navigate(object? target)
    {
        var page = target switch
        {
            AppPage p => p,
            string s when Enum.TryParse<AppPage>(s, out var x) => x,
            _ => (AppPage?)null,
        };
        if (page is { } pg && Dialog == null) CurrentPage = pg;
    }

    /// <summary>Клик по подсказке на листе: перейти к полю титульного листа.</summary>
    public void FocusField(string key)
    {
        CurrentPage = AppPage.TitlePage;
        FocusRequested?.Invoke(AppPage.TitlePage, key);
    }

    // ------------------------------------------------------------ профили

    void BuildCards()
    {
        var selectedKey = _profile?.Key;
        Cards.Clear();
        Cards.Add(new ProfileCard { Key = "kursovaya", Title = "Курсовая работа", NavTitle = "Курсовая работа", Glyph = "",
            Description = "По ГОСТ 7.32-2017. Титульный лист с дисциплиной, руководителем и оценкой." });
        Cards.Add(new ProfileCard { Key = "vkr", Title = "Выпускная квалификационная работа", NavTitle = "ВКР", Glyph = "",
            Description = "ВКР, диплом, магистерская. Титульный лист с допуском к защите и нормоконтролем." });
        Cards.Add(new ProfileCard { Key = "eskd", Title = "Пояснительная записка (ЕСКД)", NavTitle = "Пояснительная записка", Glyph = "",
            Gost = "ГОСТ Р 2.105-2019", Description = "По ГОСТ Р 2.105-2019 для курсовых и дипломных проектов в технических вузах." });
        Cards.Add(new ProfileCard { Key = "referat", Title = "Реферат", NavTitle = "Реферат", Glyph = "\uE8A5",
            Description = "По ГОСТ 7.32-2017. Обзор источников по теме: введение, две главы, заключение." });
        Cards.Add(new ProfileCard { Key = "practice", Title = "Отчёт по практике", NavTitle = "Отчёт по практике", Glyph = "\uE821",
            Description = "По ГОСТ 7.32-2017. Место и сроки, руководители, дневник практики." });
        Cards.Add(new ProfileCard { Key = "kontrolnaya", Title = "Контрольная работа", NavTitle = "Контрольная работа", Glyph = "\uE9D5",
            Description = "По ГОСТ 7.32-2017. Вариант, теория и задача с решением." });
        Cards.Add(new ProfileCard { Key = "dissertation", Title = "Диссертация", NavTitle = "Диссертация", Glyph = "\uE8F1",
            Gost = "ГОСТ Р 7.0.11-2011", Description = "По ГОСТ Р 7.0.11-2011. «На правах рукописи», номер страницы вверху." });
        foreach (var imp in _settings.Imported)
        {
            var broken = !File.Exists(imp.Path) || !TryLoad(imp.Path, out _);
            Cards.Add(new ProfileCard
            {
                Key = imp.Id, Title = imp.Name, NavTitle = imp.Name, Glyph = "", IsImported = true, Imported = imp, IsBroken = broken,
                Description = broken ? "Файл профиля повреждён" : $"Файл: {imp.OriginalFileName} · импортирован {imp.ImportedAt:dd.MM.yyyy}",
            });
        }
        foreach (var c in Cards) { c.IsSelected = c.Key == selectedKey; c.SelectRequested = card => SelectProfile(card); }
        _profile = Cards.FirstOrDefault(c => c.Key == selectedKey) ?? _profile;
        OnPropertyChanged(nameof(BuiltInCards));
        OnPropertyChanged(nameof(ImportedCards));
        OnPropertyChanged(nameof(ImportedTiles));
    }

    static bool TryLoad(string path, out Profile profile)
    {
        try { profile = ProfileJson.Load(path); return true; }
        catch (Exception) { profile = new Profile(); return false; }
    }

    void SelectProfile(ProfileCard card, bool initial = false)
    {
        if (card == _profile && !initial) return;
        Profile original;
        if (card.IsImported) { if (!TryLoad(card.Imported!.Path, out original)) return; }
        else original = BuiltInProfiles.Load(card.Key);

        if (!initial) SaveSession();
        var state = _settings.For(card.Key);
        var working = original;
        if (state.Edits != null)
        {
            try { working = ProfileJson.Parse(state.Edits, _ => throw new InvalidOperationException()); }
            catch (Exception) { state.Edits = null; }
        }

        _switching = true;
        _original = original;
        Rules.SetBaseline(original, card.IsImported ? null : card.Gost, card.Title);
        Rules.Load(working);
        Structure.Load(working);
        Fields.Load(original, _settings.Fields, state.Fields);
        _switching = false;

        foreach (var c in Cards) c.IsSelected = c == card;
        _profile = card;
        _settings.LastProfile = card.Key;
        _settings.Save();
        ClearUndo();
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(ProfileTitle));
        OnPropertyChanged(nameof(IsImportedProfile));
        Rules.ClearSampleMarks();
        _lastDetect = null;
        OnDetectedSourceChanged();
        OnPropertyChanged(nameof(ResetToProfileText));
        OnPropertyChanged(nameof(ResetButtonAccess));
        OnPropertyChanged(nameof(ResetButtonText));
        Refresh();
    }

    /// <summary>Профиль для сборки: исходный + правила + структура (+ данные титульного листа).</summary>
    public Profile BuildProfile(bool withFields = true)
    {
        var p = _original.Clone();
        Rules.ApplyTo(p);
        Structure.ApplyTo(p, _original);
        if (withFields) Fields.ApplyTo(p);
        return p;
    }

    void OnEdited(bool clearUndo)
    {
        if (_switching) return;
        if (clearUndo) ClearUndo();
        Refresh();
        _autosave.Stop();
        _autosave.Start();
    }

    void Refresh()
    {
        Preview.Update(BuildProfile(), Structure);
        var rulesOnly = BuildProfile(withFields: false);
        IsStructureModified = ProfileJson.Serialize(new Profile { Structure = rulesOnly.Structure })
                              != ProfileJson.Serialize(new Profile { Structure = _original.Structure });
        UpdateNav();
    }

    void UpdateNav()
    {
        Nav[0].Subtitle = _profile?.NavTitle ?? "";
        Nav[1].Subtitle = Fields.NavSubtitle;
        Nav[1].ErrorCount = Fields.ErrorCount;
        Nav[2].Subtitle = Rules.NavSubtitle;
        Nav[2].ErrorCount = Rules.ErrorCount;
        Nav[2].IsModified = Rules.ChangedCount > 0 && Rules.ErrorCount == 0;
        Nav[3].Subtitle = Structure.Summary;
        Nav[3].ErrorCount = Structure.ErrorCount;
        Nav[3].IsModified = IsStructureModified && Structure.ErrorCount == 0;
        OnPropertyChanged(nameof(CanResetRules));
        UpdateValidationBar();
    }

    /// <summary>Инфобар «Исправьте ошибки…»: пересчитывается и исчезает, когда ошибок не осталось.</summary>
    void UpdateValidationBar()
    {
        if (InfoBar is not { IsValidation: true }) return;
        var first = ErrorSections().FirstOrDefault(s => s.Count > 0);
        if (first.Count == 0) { InfoBar = null; return; }
        var text = $"В разделе «{first.Name}» ошибок: {first.Count}.";
        if (InfoBar.Message != text) ShowValidationBar(text);
    }

    (AppPage Page, string Name, int Count)[] ErrorSections() =>
    [
        (AppPage.TitlePage, "Титульный лист", Fields.ErrorCount),
        (AppPage.Rules, "Правила оформления", Rules.ErrorCount),
        (AppPage.Structure, "Структура работы", Structure.ErrorCount),
    ];

    void ShowValidationBar(string text)
    {
        var bar = new InfoBarViewModel { Kind = InfoKind.Error, Title = "Исправьте ошибки перед созданием шаблона", Message = text, IsValidation = true };
        bar.Close = () => { if (InfoBar == bar) InfoBar = null; };
        InfoBar = bar;
    }

    public void SaveSession()
    {
        if (_profile == null) return;
        var state = _settings.For(_profile.Key);
        var hasErrors = Rules.HasErrors || Structure.ErrorCount > 0;
        if (!hasErrors)
        {
            var edited = BuildProfile(withFields: false);
            state.Edits = ProfileJson.Serialize(edited) != ProfileJson.Serialize(_original) ? ProfileJson.Serialize(edited) : null;
        }
        Fields.Remember(_settings.Fields, state.Fields);
        _settings.Save();
    }

    // ------------------------------------------------------------ инфобары и отмена

    void ShowInfo(InfoKind kind, string? title, string? message, string? actionText = null, Action? action = null)
    {
        var bar = new InfoBarViewModel { Kind = kind, Title = title, Message = message, ActionText = actionText, Action = action };
        bar.Close = () => { if (InfoBar == bar) InfoBar = null; };
        InfoBar = bar;
    }

    void ShowUndo(string text, Action undo)
    {
        _undo = undo;
        var bar = new InfoBarViewModel { Kind = InfoKind.Informational, Message = text, ActionText = "Отменить", IsUndo = true, Action = Undo };
        bar.Close = ClearUndo;
        InfoBar = bar;
    }

    void Undo()
    {
        var undo = _undo;
        ClearUndo();
        undo?.Invoke();
    }

    void ClearUndo()
    {
        _undo = null;
        if (InfoBar?.IsUndo == true) InfoBar = null;
    }

    public void ResetRules(string target)
    {
        var snapshot = BuildProfile(withFields: false);
        var toGost = target == "gost" || !IsImportedProfile;
        var baseline = toGost && IsImportedProfile ? BuiltInProfiles.Load("kursovaya") : _original;
        _switching = true;
        Rules.Load(baseline);
        _switching = false;
        Refresh();
        _autosave.Start();
        var marks = Rules.SampleMarks;
        var state = _settings.For(_profile!.Key);
        var (prevSource, prevVerdict) = (state.DetectedSource, state.DetectedVerdict);
        Rules.ClearSampleMarks();
        state.DetectedSource = state.DetectedVerdict = null;
        OnDetectedSourceChanged();
        var text = toGost ? $"Правила сброшены к {(IsImportedProfile ? RulesViewModel.DefaultGost : _profile.Gost)}." : $"Правила сброшены к профилю «{ProfileTitle}».";
        ShowUndo(text, () =>
        {
            _switching = true;
            Rules.Load(snapshot);
            _switching = false;
            Rules.RestoreSampleMarks(marks);
            state.DetectedSource = prevSource;
            state.DetectedVerdict = prevVerdict;
            OnDetectedSourceChanged();
            Refresh();
            _autosave.Start();
        });
    }

    void ResetStructure()
    {
        var snapshot = BuildProfile(withFields: false);
        _switching = true;
        Structure.Load(_original);
        _switching = false;
        Refresh();
        _autosave.Start();
        ShowUndo($"Структура возвращена к профилю «{ProfileTitle}».", () =>
        {
            _switching = true;
            Structure.Load(snapshot);
            _switching = false;
            Refresh();
            _autosave.Start();
        });
    }

    async Task ClearFieldsAsync()
    {
        var answer = await AskAsync("Очистить титульный лист?",
            "Все введённые данные — вуз, тема, ФИО и другие — будут удалены. Отменить это действие нельзя.",
            [new DialogButton { Text = "_Очистить", Result = "ok" }, new DialogButton { Text = "Отмена", Result = "cancel", IsCancel = true, IsDefaultFocus = true }]);
        if (answer != "ok") return;
        Fields.Clear();
        SaveSession();
        ShowInfo(InfoKind.Success, null, "Поля титульного листа очищены.");
    }

    // ------------------------------------------------------------ диалоги

    public void ShowDialog(DialogViewModel dialog)
    {
        // Открытый диалог (например, «Создание шаблона») не вытесняется горячими клавишами
        if (Dialog != null && Dialog != dialog) return;
        dialog.CloseRequested = () => { if (Dialog == dialog) Dialog = null; };
        Dialog = dialog;
    }

    Task<string> AskAsync(string title, string text, IReadOnlyList<DialogButton> buttons, DialogIcon icon = DialogIcon.None)
    {
        var dialog = new MessageDialogViewModel { Title = title, Text = text, Buttons = buttons, Icon = icon };
        ShowDialog(dialog);
        return dialog.Result;
    }

    /// <summary>Закрытие окна во время создания шаблона.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsBusy) { SaveSession(); return true; }
        var answer = await AskAsync("Шаблон ещё создаётся", "Если закрыть приложение сейчас, шаблон не будет сохранён.",
        [
            new DialogButton { Text = "_Подождать", Result = "wait", IsPrimary = true, IsDefaultFocus = true, IsCancel = true },
            new DialogButton { Text = "Закрыть _приложение", Result = "close" },
        ]);
        if (answer != "close") return false;
        _cts?.Cancel();
        SaveSession();
        return true;
    }

    // ------------------------------------------------------------ импорт и профили кафедр

    void ImportInteractive()
    {
        var path = _dialogs.OpenFile("Импортировать профиль оформления", ProfileFilter);
        if (path != null) _ = ImportAsync(path);
    }

    public async Task ImportAsync(string path)
    {
        Profile profile;
        try
        {
            profile = ProfileJson.LoadForImport(path);
        }
        catch (ProfileImportException e)
        {
            var answer = await AskAsync("Не удалось импортировать профиль", e.Message,
            [
                new DialogButton { Text = "_Выбрать другой файл", Result = "again", IsPrimary = true, IsDefaultFocus = true },
                new DialogButton { Text = "Закрыть", Result = "cancel", IsCancel = true },
            ], DialogIcon.Error);
            if (answer == "again") ImportInteractive();
            return;
        }

        var name = string.IsNullOrWhiteSpace(profile.Name) ? Path.GetFileNameWithoutExtension(path) : profile.Name.Trim();
        var existing = _settings.Imported.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var answer = await AskAsync("Профиль с таким названием уже есть", $"В списке уже есть профиль «{name}». Заменить его новым файлом?",
            [
                new DialogButton { Text = "_Заменить", Result = "replace", IsPrimary = true, IsDefaultFocus = true },
                new DialogButton { Text = "Оставить _оба", Result = "both" },
                new DialogButton { Text = "Отмена", Result = "cancel", IsCancel = true },
            ]);
            if (answer == "cancel") return;
            if (answer == "both")
            {
                var n = 2;
                while (_settings.Imported.Any(i => i.Name == $"{name} ({n})")) n++;
                name = $"{name} ({n})";
                existing = null;
            }
        }

        profile.Name = name;
        AddImported(profile, Path.GetFileName(path), existing);
        ShowInfo(InfoKind.Success, null, $"Профиль «{name}» импортирован и выбран.");
    }

    void AddImported(Profile profile, string originalFileName, ImportedProfile? replace)
    {
        Directory.CreateDirectory(AppSettings.ProfilesDir);
        var entry = replace ?? new ImportedProfile { Id = "imp-" + Guid.NewGuid().ToString("N")[..8] };
        entry.Name = profile.Name;
        entry.OriginalFileName = originalFileName;
        entry.ImportedAt = DateTime.Now;
        entry.Path = Path.Combine(AppSettings.ProfilesDir, entry.Id + ".json");
        ProfileJson.Save(profile, entry.Path); // копия без «extends» — не зависит от исходной папки
        if (replace == null) _settings.Imported.Add(entry);
        else _settings.For(entry.Id).Edits = null;
        _settings.Save();
        BuildCards();
        var card = Cards.First(c => c.Key == entry.Id);
        _profile = null;
        SelectProfile(card);
    }

    async Task RemoveProfileAsync(ProfileCard? card)
    {
        if (card?.Imported == null) return;
        var answer = await AskAsync("Удалить профиль из списка?",
            $"Профиль «{card.Title}» исчезнет из списка видов работ. Исходный файл на диске останется.",
            [new DialogButton { Text = "_Удалить", Result = "ok" }, new DialogButton { Text = "Отмена", Result = "cancel", IsCancel = true, IsDefaultFocus = true }]);
        if (answer != "ok") return;
        _settings.Imported.Remove(card.Imported);
        _settings.Profiles.Remove(card.Key);
        try { File.Delete(card.Imported.Path); } catch (IOException) { }
        var wasSelected = card == _profile;
        _settings.Save();
        BuildCards();
        if (wasSelected) SelectProfile(Cards[0]);
    }

    /// <summary>Профиль для файла: без данных студента; данные вуза — только по желанию.</summary>
    Profile ExportProfile(string name, bool includeOrg)
    {
        var p = BuildProfile(withFields: false);
        p.Name = name;
        if (includeOrg)
            foreach (var key in new[] { "university", "institute", "department", "city", "ministry", "org_form" })
                if (Fields.Find(key) is { IsFilled: true } f) p.Fields[key] = f.Value.Trim();
        return p;
    }

    void SaveProfileDialog()
    {
        if (Rules.HasErrors) { _ = ShowValidationErrorAsync(); return; }
        var detected = _profile != null ? _settings.For(_profile.Key).DetectedSource : null;
        var dialog = new SaveProfileDialogViewModel(detected != null
            ? $"{ProfileTitle} — по примеру «{Path.GetFileNameWithoutExtension(detected)}»"
            : $"{ProfileTitle} — моя методичка");
        dialog.SaveCommand = new RelayCommand(() =>
        {
            if (!dialog.Validate()) return;
            var name = dialog.Name.Trim();
            var path = _dialogs.SaveFile("Сохранить профиль оформления", ProfileFilter, SafeFileName(name + ".json"), _settings.LastDirectory);
            if (path == null) return;
            try
            {
                var profile = ExportProfile(name, dialog.IncludeOrg);
                ProfileJson.Save(profile, path);
                dialog.Dismiss();
                AddImported(profile, Path.GetFileName(path), _settings.Imported.FirstOrDefault(i => i.Name == name));
                ShowInfo(InfoKind.Success, "Профиль сохранён", path, "Показать в папке", () => _dialogs.ShowInFolder(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                dialog.Dismiss();
                ShowInfo(InfoKind.Error, "Не удалось сохранить профиль", e.Message);
            }
        });
        ShowDialog(dialog);
    }

    void ShareDialog()
    {
        if (Rules.HasErrors) { _ = ShowValidationErrorAsync(); return; }
        var name = $"{ProfileTitle} — моя методичка";
        var dialog = new ShareDialogViewModel(SafeFileName(name + ".json"));
        var dir = Path.Combine(Path.GetTempPath(), "Chistovik");
        var temp = Path.Combine(dir, dialog.FileName);
        void Write()
        {
            Directory.CreateDirectory(dir);
            ProfileJson.Save(ExportProfile(name, dialog.IncludeOrg), temp);
            dialog.SizeText = ShareDialogViewModel.FormatSize(new FileInfo(temp).Length);
            dialog.Title = dialog.Title; // обновить привязки
        }
        Write();
        dialog.IncludeOrgChanged = Write;
        dialog.CopyCommand = new RelayCommand(() => { Write(); _dialogs.CopyFileToClipboard(temp); dialog.Copied = true; });
        dialog.SaveAsCommand = new RelayCommand(() =>
        {
            var path = _dialogs.SaveFile("Сохранить профиль оформления", ProfileFilter, dialog.FileName, _settings.LastDirectory);
            if (path == null) return;
            Write();
            File.Copy(temp, path, true);
            dialog.Dismiss();
            ShowInfo(InfoKind.Success, "Профиль сохранён", path, "Показать в папке", () => _dialogs.ShowInFolder(path));
        });
        ShowDialog(dialog);
    }

    // ------------------------------------------------------------ определение правил по примеру (DESIGN.md 17)

    DetectDialogViewModel CreateDetectDialog()
    {
        var dialog = new DetectDialogViewModel(Rules, _dialogs, ProfileTitle, ApplyDetection, SuggestProfile);
        dialog.CloseRequested = () => { if (Dialog == dialog) Dialog = null; };
        return dialog;
    }

    /// <summary>Встроенный вид работы по каждому эталону ГОСТ.</summary>
    static readonly Dictionary<string, string> ProfileForStandard = new()
    {
        [Standards.Gost732.Id] = "kursovaya",
        [Standards.Gost2105.Id] = "eskd",
        [Standards.Gost7011.Id] = "dissertation",
    };

    /// <summary>
    /// Пример похож на ГОСТ, под который есть готовый вид работы, а выбран вид по другому ГОСТу
    /// (например, пример — диссертация, выбрана курсовая) — предлагаем сменить вид работы.
    /// Профили кафедр не подменяем: у них свои правила.
    /// </summary>
    (string Key, string Title)? SuggestProfile(StandardVerdict? verdict)
    {
        if (verdict?.Standard is not { } standard || _profile is not { IsImported: false } current) return null;
        if (current.Gost == standard.Name) return null;
        if (!ProfileForStandard.TryGetValue(standard.Id, out var key)) return null;
        return Cards.FirstOrDefault(c => c.Key == key) is { } card ? (card.Key, card.Title) : null;
    }

    public void OpenDetect(DetectSource source)
    {
        if (Dialog != null) return;
        var dialog = CreateDetectDialog();
        dialog.Source = source;
        ShowDialog(dialog);
    }

    /// <summary>Перетаскивание или запуск с файлом: диалог сразу на этом файле.</summary>
    public void OpenDetectFile(string path)
    {
        if (Dialog is DetectDialogViewModel open && !open.IsAnalyzing) { _ = open.AnalyzeFileAsync(path); return; }
        if (Dialog != null) return;
        var dialog = CreateDetectDialog();
        ShowDialog(dialog);
        _ = dialog.AnalyzeFileAsync(path);
    }

    void OpenDetectFromClipboard()
    {
        if (Dialog != null) return;
        var dialog = CreateDetectDialog();
        dialog.Source = DetectSource.Text;
        ShowDialog(dialog);
        var text = _dialogs.GetClipboardText();
        if (text != null && text.Count(c => !char.IsWhiteSpace(c)) >= 20)
        {
            dialog.Text = text;
            _ = dialog.AnalyzeTextAsync();
        }
    }

    void ShowLastDetectResult()
    {
        if (_lastDetect is not { } last || Dialog != null) return;
        var dialog = CreateDetectDialog();
        if (last.Source == DetectSource.Text) dialog.Text = last.Text;
        ShowDialog(dialog);
        dialog.ShowResult(last.Result, last.Source);
    }

    void ApplyDetection(DetectDialogViewModel dialog)
    {
        if (dialog.Result is not { } result) return;
        var chosen = dialog.Rows.Where(r => r.IsChecked).Select(r => r.Rule).ToList();
        // Смена вида работы под найденный ГОСТ: сначала переключаем профиль (титул и структура по стандарту),
        // затем применяем правила примера — включая «уже так»: они совпадали со старым профилем, не с новым
        var previousCard = _profile;
        var checkedCount = chosen.Count; // столько правил пользователь отметил — их и называем в сообщении
        var switchTo = dialog.SuggestedProfile is { } sp ? Cards.FirstOrDefault(c => c.Key == sp.Key) : null;
        if (switchTo != null)
        {
            chosen = dialog.Rows.Where(r => r.IsChecked || r.IsSame).Select(r => r.Rule).ToList();
            SelectProfile(switchTo);
        }
        if (chosen.Count == 0 && switchTo == null) return;
        var snapshot = BuildProfile(withFields: false);
        var marks = Rules.SampleMarks;
        var state = _settings.For(_profile!.Key);
        var (prevSource, prevVerdict) = (state.DetectedSource, state.DetectedVerdict);

        Rules.ApplyDetected(chosen);
        var source = dialog.Source == DetectSource.File ? dialog.FileName : "текст требований";
        state.DetectedSource = source;
        state.DetectedVerdict = result.Verdict?.Kind is VerdictKind.Matches or VerdictKind.CustomRules ? result.Verdict.Title : null;
        _lastDetect = (result, dialog.Source, null, dialog.Text);
        _settings.Save();
        OnDetectedSourceChanged();
        dialog.Dismiss();

        CurrentPage = AppPage.Rules;
        Preview.Mode = PreviewMode.Page;
        OnPropertyChanged(nameof(PreviewModeIndex));
        var verdict = result.Verdict is { Kind: VerdictKind.Matches } v ? $" {v.Title}." : "";
        var switched = switchTo != null ? $"Вид работы: «{switchTo.Title}». " : "";
        ShowUndo(checkedCount == 0 ? $"{switched}Правила из примера «{source}» применены.{verdict}" : $"{switched}Применено правил: {checkedCount} из примера «{source}».{verdict}", () =>
        {
            _switching = true;
            Rules.Load(snapshot);
            _switching = false;
            Rules.RestoreSampleMarks(marks);
            state.DetectedSource = prevSource;
            state.DetectedVerdict = prevVerdict;
            _settings.Save();
            OnDetectedSourceChanged();
            Refresh();
            _autosave.Start();
            if (switchTo != null && previousCard != null) SelectProfile(previousCard);
        });
        if (chosen.Count > 0) FocusRequested?.Invoke(AppPage.Rules, "rule:" + chosen[0].Key);
    }

    void OnDetectedSourceChanged()
    {
        OnPropertyChanged(nameof(DetectedSourceText));
        OnPropertyChanged(nameof(HasDetectedSource));
        OnPropertyChanged(nameof(CanShowDetectResult));
    }

    // ------------------------------------------------------------ параметры

    void ChangeDefaultFolder()
    {
        var folder = _dialogs.PickFolder("Папка для шаблонов", DefaultFolder);
        if (folder == null) return;
        _settings.DefaultDirectory = folder;
        _settings.LastDirectory = null;
        _settings.Save();
        OnPropertyChanged(nameof(DefaultFolder));
    }

    async Task ClearDataAsync()
    {
        var answer = await AskAsync("Очистить сохранённые данные?",
            "Будут удалены поля титульного листа, изменённые правила и структура, а также импортированные профили. Отменить это действие нельзя.",
            [new DialogButton { Text = "_Очистить", Result = "ok" }, new DialogButton { Text = "Отмена", Result = "cancel", IsCancel = true, IsDefaultFocus = true }]);
        if (answer != "ok") return;
        _autosave.Stop();
        AppSettings.DeleteAll();
        _settings.Fields.Clear();
        _settings.Profiles.Clear();
        _settings.Imported.Clear();
        _settings.LastFile = null;
        _settings.DefaultDirectory = null;
        _settings.LastDirectory = null;
        _settings.Save();
        BuildCards();
        _profile = null;
        SelectProfile(Cards[0]);
        OnPropertyChanged(nameof(DefaultFolder));
        OnPropertyChanged(nameof(LastFileExists));
        ShowInfo(InfoKind.Success, null, "Сохранённые данные удалены.");
    }

    // ------------------------------------------------------------ создание шаблона

    async Task<bool> ShowValidationErrorAsync()
    {
        var first = ErrorSections().FirstOrDefault(s => s.Count > 0);
        if (first.Count == 0) return false;
        CurrentPage = first.Page;
        ShowValidationBar($"В разделе «{first.Name}» ошибок: {first.Count}.");
        await Task.Yield();
        FocusRequested?.Invoke(first.Page, "error");
        return true;
    }

    async Task GenerateAsync()
    {
        if (IsBusy || Dialog != null) return;
        if (await ShowValidationErrorAsync()) return;
        SaveSession();
        var student = Fields.Find("student") is { IsFilled: true } s ? s.Value.Trim() : null;
        var name = SafeFileName($"{_profile?.Title ?? "Шаблон"}{(student != null ? " – " + student : "")}.docx");
        var dir = Directory.Exists(_settings.LastDirectory) ? _settings.LastDirectory : DefaultFolder;
        var path = _dialogs.SaveFile("Сохранить шаблон", DocxFilter, name, dir);
        if (path == null) return;
        _settings.LastDirectory = Path.GetDirectoryName(path);
        _settings.Save();
        await RunGenerationAsync(BuildProfile(), path);
    }

    async Task RunGenerationAsync(Profile profile, string path)
    {
        var pdf = SavePdf && IsWordInstalled;
        var dialog = new GenerationDialogViewModel(pdf);
        _cts = new CancellationTokenSource();
        var cts = _cts;
        dialog.CancelCommand = new RelayCommand(() => cts.Cancel());
        ShowDialog(dialog);
        IsBusy = true;

        var slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        slow.Tick += (_, _) => { slow.Stop(); dialog.Hint = "Word отвечает дольше обычного. Подождите ещё немного или отмените."; };
        slow.Start();

        var steps = dialog.Steps;
        steps[0].State = StepState.Running;
        var progress = new Progress<GenerationStage>(stage =>
        {
            switch (stage)
            {
                case GenerationStage.UpdatingFields:
                    steps[0].State = StepState.Done;
                    steps[1].State = StepState.Running;
                    break;
                case GenerationStage.ExportingPdf when steps.Count > 2:
                    steps[1].State = StepState.Done;
                    steps[2].State = StepState.Running;
                    break;
            }
        });

        try
        {
            var result = await Task.Run(() => TemplateService.Generate(profile, path, pdf, useWord: true, progress, cts.Token));
            ShowResult(dialog, result, pdf);
        }
        catch (OperationCanceledException)
        {
            dialog.Dismiss();
            ShowInfo(InfoKind.Informational, "Создание шаблона отменено.", "Файл не сохранён.");
        }
        catch (FileLockedException)
        {
            Fail(dialog, GenerationPhase.Locked, "Не удалось сохранить шаблон",
                $"Файл «{Path.GetFileName(path)}» открыт в Word или другой программе. Закройте его и нажмите «Повторить» — или сохраните шаблон под другим именем.");
            dialog.RetryCommand = new RelayCommand(async () => { dialog.Dismiss(); await RunGenerationAsync(profile, path); });
            dialog.SaveAsCommand = new RelayCommand(async () => { dialog.Dismiss(); await SaveAsAndRun(profile, path); });
        }
        catch (UnauthorizedAccessException)
        {
            Fail(dialog, GenerationPhase.NoAccess, "Нет доступа к папке",
                $"В папку «{Path.GetFileName(Path.GetDirectoryName(path))}» нельзя сохранить файл. Выберите другую, например «Документы».");
            dialog.SaveAsCommand = new RelayCommand(async () => { dialog.Dismiss(); await SaveAsAndRun(profile, path); });
        }
        catch (Exception e)
        {
            Fail(dialog, GenerationPhase.Error, "Не удалось создать шаблон",
                "При сборке документа произошла ошибка. Попробуйте ещё раз. Если ошибка повторится, отправьте подробности разработчику.");
            dialog.Details = e.ToString();
            dialog.CopyDetailsCommand = new RelayCommand(() => { _dialogs.CopyText(e.ToString()); dialog.Copied = true; });
            dialog.RetryCommand = new RelayCommand(async () => { dialog.Dismiss(); await RunGenerationAsync(profile, path); });
        }
        finally
        {
            slow.Stop();
            IsBusy = false;
            if (_cts == cts) _cts = null;
            cts.Dispose();
            dialog.CloseCommand = new RelayCommand(dialog.Dismiss);
            dialog.Refresh();
        }
    }

    async Task SaveAsAndRun(Profile profile, string previous)
    {
        var path = _dialogs.SaveFile("Сохранить шаблон", DocxFilter, Path.GetFileName(previous), DefaultFolder);
        if (path == null) return;
        _settings.LastDirectory = Path.GetDirectoryName(path);
        await RunGenerationAsync(profile, path);
    }

    void Fail(GenerationDialogViewModel dialog, GenerationPhase phase, string title, string text)
    {
        foreach (var s in dialog.Steps.Where(s => s.State == StepState.Running)) s.State = StepState.Failed;
        dialog.Phase = phase;
        dialog.Icon = DialogIcon.Error;
        dialog.Title = title;
        dialog.Text = text;
    }

    void ShowResult(GenerationDialogViewModel dialog, GenerationResult result, bool pdfRequested)
    {
        var steps = dialog.Steps;
        steps[0].State = StepState.Done;
        dialog.DocxPath = result.DocxPath;
        dialog.DocxName = Path.GetFileName(result.DocxPath);
        dialog.Folder = PathText.Middle(Path.GetDirectoryName(result.DocxPath) ?? "", 52);
        dialog.PdfPath = result.PdfPath;
        dialog.PdfName = result.PdfPath != null ? Path.GetFileName(result.PdfPath) : null;
        dialog.OpenCommand = new RelayCommand(() => { _dialogs.OpenPath(result.DocxPath); dialog.Dismiss(); });
        dialog.OpenPdfCommand = new RelayCommand(() => { if (result.PdfPath != null) _dialogs.OpenPath(result.PdfPath); });
        dialog.ShowInFolderCommand = new RelayCommand(() => _dialogs.ShowInFolder(result.DocxPath));

        if (result.FieldsUpdated)
        {
            steps[1].State = StepState.Done;
            if (steps.Count > 2) steps[2].State = result.PdfPath != null ? StepState.Done : StepState.Skipped;
            dialog.Phase = GenerationPhase.Success;
            dialog.Icon = DialogIcon.Success;
            dialog.Title = "Шаблон готов";
            dialog.Text = "Содержание, номера страниц, рисунков и таблиц уже обновлены.";
        }
        else
        {
            steps[1].State = StepState.Skipped;
            steps[1].Text = "Word не найден — содержание обновится при открытии";
            if (steps.Count > 2) { steps[2].State = StepState.Skipped; steps[2].Text = "PDF не сохранён — нужен Microsoft Word"; }
            dialog.Phase = GenerationPhase.Warning;
            dialog.Icon = DialogIcon.Warning;
            dialog.Title = "Шаблон создан";
            dialog.WarningTitle = "Содержание обновится при открытии";
            const string tail = "Когда вы откроете файл, Word спросит, обновить ли поля, — нажмите «Да».";
            dialog.WarningText = result.WordIssue switch
            {
                WordIssue.TimedOut => "Word не ответил вовремя, поэтому содержание и номера рисунков пока не заполнены. " + tail,
                WordIssue.Failed => "Word не смог обновить документ, поэтому содержание и номера рисунков пока не заполнены. " + tail,
                _ => "Microsoft Word не найден, поэтому содержание и номера рисунков пока не заполнены. " + tail,
            };
            if (result.WordIssue == WordIssue.TimedOut) steps[1].Text = "Word не ответил — содержание обновится при открытии";
            if (result.WordIssue == WordIssue.Failed) steps[1].Text = "Word не смог обновить содержание — оно обновится при открытии";
            if (steps.Count > 2 && result.WordIssue != WordIssue.NotInstalled) steps[2].Text = "PDF не сохранён — Word не завершил экспорт";
            dialog.WarningPdf = !(pdfRequested || result.PdfRequested) ? ""
                : result.WordIssue == WordIssue.NotInstalled ? "PDF не сохранён: для него нужен Microsoft Word."
                : "PDF не сохранён: Word не завершил экспорт.";
            dialog.Text = "";
        }

        _settings.LastFile = result.DocxPath;
        _settings.Save();
        OnPropertyChanged(nameof(LastFileExists));
        OnPropertyChanged(nameof(LastFileName));
        OnPropertyChanged(nameof(LastFilePath));

        if (OpenAfter)
        {
            _dialogs.OpenPath(result.DocxPath);
            dialog.OpenedAutomatically = true;
            if (dialog.Phase == GenerationPhase.Success) dialog.Text = "Шаблон открыт в Word.";
        }
    }

    static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
