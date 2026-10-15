using System.Collections.ObjectModel;
using ShablonRabot.App.Infrastructure;
using ShablonRabot.App.Services;
using ShablonRabot.Core;
using ShablonRabot.Core.Ai;

namespace ShablonRabot.App.ViewModels;

/// <summary>Задача для ИИ — плитка на странице.</summary>
public sealed record AiTaskCard(WritingTask Task, string Title, string Glyph, string Description);

/// <summary>Параграф из структуры работы, для которого можно написать черновик.</summary>
public sealed record SectionChoice(int Chapter, int Section, string Title, string Label, bool HasDraft)
{
    public override string ToString() => Label;
}

/// <summary>Черновик, который попадёт в шаблон.</summary>
public sealed record DraftItem(string Key, string Title, string Info, bool IsOrphan);

/// <summary>
/// Раздел «ИИ-помощник»: подключение по API-ключу, задачи (план, введение, параграф, заключение, правка текста, литература)
/// и черновики, которые при создании шаблона займут место подсказок.
/// </summary>
public sealed class AiViewModel : ObservableObject
{
    readonly AppSettings _settings;
    readonly IDialogService _dialogs;
    readonly Func<WorkContext> _context;
    readonly Func<ProfileState> _state;
    readonly Action<List<ChapterPlan>> _applyPlan;
    readonly Action<InfoKind, string?, string> _info;
    string _providerId, _baseUrl, _model, _apiKey = "", _input = "", _result = "", _notes = "";
    string? _connectError, _connectErrorTitle, _errorTitle, _errorText, _resultKey, _resultHeader, _connectInfo;
    bool _setupOpen, _testing, _running, _stopped;
    WritingTask _task = WritingTask.Plan, _resultTask;
    SectionChoice? _section;
    DraftLength _length = DraftLength.Medium;
    WorkContext _work = new();
    CancellationTokenSource? _cts;

    public AiViewModel(AppSettings settings, IDialogService dialogs, Func<WorkContext> context, Func<ProfileState> state,
        Action<List<ChapterPlan>> applyPlan, Action<InfoKind, string?, string> info)
    {
        _settings = settings;
        _dialogs = dialogs;
        _context = context;
        _state = state;
        _applyPlan = applyPlan;
        _info = info;
        var ai = settings.Ai;
        _providerId = AiProviders.Find(ai.Provider).Id;
        _baseUrl = ai.BaseUrl ?? Provider.BaseUrl;
        _model = ai.Model ?? Provider.Model;
        foreach (var m in ai.Models ?? []) Models.Add(m);
        _setupOpen = !IsConnected;

        ConnectCommand = new RelayCommand(async () => await ConnectAsync(), () => !_testing && CanConnect);
        ChangeConnectionCommand = new RelayCommand(() => { ConnectError = null; IsSetupOpen = true; });
        CancelSetupCommand = new RelayCommand(CancelSetup, () => IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        OpenKeysPageCommand = new RelayCommand(() => { if (Provider.KeysUrl is { } url) _dialogs.OpenPath(url); }, () => Provider.KeysUrl != null);
        RunCommand = new RelayCommand(async () => await RunAsync(), () => CanRun);
        StopCommand = new RelayCommand(() => { _stopped = true; _cts?.Cancel(); }, () => IsRunning);
        InsertCommand = new RelayCommand(Insert, () => CanInsert);
        ApplyPlanCommand = new RelayCommand(ApplyPlan, () => ShowApplyPlan && !IsRunning);
        CopyCommand = new RelayCommand(() => _dialogs.CopyText(Result), () => HasResult && !IsRunning);
        ClearResultCommand = new RelayCommand(() => { Result = ""; _resultKey = null; ResultHeader = null; OnResultChanged(); }, () => HasResult && !IsRunning);
        EditDraftCommand = new RelayCommand(p => EditDraft(p as DraftItem), _ => !IsRunning);
        RemoveDraftCommand = new RelayCommand(p => RemoveDraft(p as DraftItem));
    }

    // ------------------------------------------------------------ подключение

    public static AiProviderInfo[] Providers => AiProviders.All;
    public AiProviderInfo Provider => AiProviders.Find(_providerId);
    public bool IsConnected => _settings.Ai.Key != null || (AiProviders.Find(_settings.Ai.Provider).IsCustom && _settings.Ai.BaseUrl != null);
    public bool IsSetupOpen { get => _setupOpen; set { if (Set(ref _setupOpen, value)) OnPropertyChanged(nameof(ShowWork)); } }
    public bool ShowWork => IsConnected && !IsSetupOpen;
    public bool IsTesting { get => _testing; private set => Set(ref _testing, value); }

    public string ProviderId
    {
        get => _providerId;
        set
        {
            if (value == null || !Set(ref _providerId, value)) return;
            BaseUrl = Provider.BaseUrl;
            Model = Provider.Model;
            Models.Clear();
            ConnectError = null;
            OnPropertyChanged(nameof(Provider));
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(KeyHint));
            OnPropertyChanged(nameof(HasKeysPage));
        }
    }
    public bool IsCustom => Provider.IsCustom;
    public bool HasKeysPage => Provider.KeysUrl != null;
    public string KeyHint => Provider.Hint;
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value ?? ""); }
    public string Model { get => _model; set => Set(ref _model, value ?? ""); }
    public ObservableCollection<string> Models { get; } = new();
    /// <summary>Ключ из поля ввода; в настройки попадает только зашифрованным.</summary>
    public string ApiKey { get => _apiKey; set { if (Set(ref _apiKey, value ?? "")) ConnectError = null; } }
    /// <summary>Уже сохранённый ключ: поле можно оставить пустым, чтобы сменить только модель.</summary>
    public bool HasSavedKey => _settings.Ai.Key != null && _settings.Ai.Provider == _providerId;
    public string KeyPlaceholder => HasSavedKey ? "Ключ сохранён — оставьте пустым, чтобы не менять" : "Вставьте API-ключ";
    bool CanConnect => (ApiKey.Trim().Length > 0 || HasSavedKey || IsCustom) && Model.Trim().Length > 0 && (!IsCustom || BaseUrl.Trim().Length > 0);

    public string? ConnectError { get => _connectError; private set { if (Set(ref _connectError, value)) OnPropertyChanged(nameof(HasConnectError)); } }
    public string? ConnectErrorTitle { get => _connectErrorTitle; private set => Set(ref _connectErrorTitle, value); }
    public bool HasConnectError => ConnectError != null;
    public string? ConnectInfo { get => _connectInfo; private set => Set(ref _connectInfo, value); }

    public string ConnectionText => $"{AiProviders.Find(_settings.Ai.Provider).Title} · {_settings.Ai.Model ?? Provider.Model}";

    public RelayCommand ConnectCommand { get; }
    public RelayCommand ChangeConnectionCommand { get; }
    public RelayCommand CancelSetupCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand OpenKeysPageCommand { get; }

    AiConnection? SavedConnection()
    {
        var ai = _settings.Ai;
        var p = AiProviders.Find(ai.Provider);
        var key = KeyProtector.Unprotect(ai.Key);
        if (key == null && !(p.IsCustom && ai.BaseUrl != null)) return null;
        return new AiConnection(p.Api, ai.BaseUrl ?? p.BaseUrl, ai.Model ?? p.Model, key ?? "");
    }

    async Task ConnectAsync()
    {
        var key = ApiKey.Trim();
        if (key.Length == 0 && HasSavedKey) key = KeyProtector.Unprotect(_settings.Ai.Key) ?? "";
        var connection = new AiConnection(Provider.Api, BaseUrl.Trim(), Model.Trim(), key);
        IsTesting = true;
        ConnectError = null;
        List<string>? models = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            models = (await AiClient.ListModelsAsync(connection, cts.Token)).Order().ToList();
        }
        catch (AiException e) when (e.Title == "Не найдено" && Provider.Api == AiApi.OpenAi)
        {
            // Не все совместимые сервисы отдают список моделей — ключ проверится первым запросом
        }
        catch (AiException e)
        {
            ConnectErrorTitle = e.Title;
            ConnectError = e.Message;
            IsTesting = false;
            return;
        }
        catch (OperationCanceledException)
        {
            ConnectErrorTitle = "Сервис не ответил вовремя";
            ConnectError = "Проверьте интернет и адрес сервиса и попробуйте ещё раз.";
            IsTesting = false;
            return;
        }
        IsTesting = false;

        var ai = _settings.Ai;
        ai.Provider = _providerId;
        ai.BaseUrl = BaseUrl.Trim() == Provider.BaseUrl ? null : BaseUrl.Trim();
        if (IsCustom) ai.BaseUrl = BaseUrl.Trim();
        ai.Model = Model.Trim();
        ai.Key = key.Length > 0 ? KeyProtector.Protect(key) : null;
        ai.Models = models is { Count: > 0 } ? models : null;
        _settings.Save();
        Models.Clear();
        foreach (var m in ai.Models ?? []) Models.Add(m);
        ApiKey = "";
        ConnectInfo = models is { Count: > 0 } && !models.Contains(ai.Model)
            ? $"Модели «{ai.Model}» нет в списке сервиса — если запрос не пройдёт, выберите другую."
            : null;
        OnConnectionChanged();
        IsSetupOpen = false;
        _info(InfoKind.Success, null, $"ИИ подключён: {ConnectionText}.");
    }

    void CancelSetup()
    {
        // Вернуть поля к сохранённому подключению
        var ai = _settings.Ai;
        _providerId = AiProviders.Find(ai.Provider).Id;
        BaseUrl = ai.BaseUrl ?? Provider.BaseUrl;
        Model = ai.Model ?? Provider.Model;
        Models.Clear();
        foreach (var m in ai.Models ?? []) Models.Add(m);
        ApiKey = "";
        ConnectError = null;
        OnPropertyChanged(string.Empty);
        IsSetupOpen = false;
    }

    void Disconnect()
    {
        _settings.Ai.Key = null;
        if (Provider.IsCustom) _settings.Ai.BaseUrl = null;
        _settings.Save();
        ApiKey = "";
        OnConnectionChanged();
        IsSetupOpen = true;
        _info(InfoKind.Informational, null, "ИИ отключён, ключ удалён с компьютера.");
    }

    void OnConnectionChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ShowWork));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(HasSavedKey));
        OnPropertyChanged(nameof(KeyPlaceholder));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RunHint));
    }

    /// <summary>Для «Параметров»: подключён ли ИИ.</summary>
    public string StatusText => IsConnected ? $"Подключено: {ConnectionText}" : "Не подключён";

    /// <summary>«Очистить сохранённые данные» удалила и ключ.</summary>
    public void Reset()
    {
        _providerId = AiProviders.All[0].Id;
        _baseUrl = Provider.BaseUrl;
        _model = Provider.Model;
        Models.Clear();
        _apiKey = "";
        _result = "";
        _resultKey = null;
        _setupOpen = true;
        OnPropertyChanged(string.Empty);
        Refresh();
    }

    // ------------------------------------------------------------ о работе

    public string WorkType => _work.WorkType;
    public string? Topic => _work.Topic;
    public bool HasTopic => !string.IsNullOrWhiteSpace(_work.Topic);
    public string TopicText => HasTopic ? $"«{WritingAssistant.Unquote(_work.Topic!.Trim())}»" : "Тема не указана";
    public string? DisciplineText => string.IsNullOrWhiteSpace(_work.Discipline) ? null : $"Дисциплина: {WritingAssistant.Unquote(_work.Discipline.Trim())}";
    public string PlanText => _work.Chapters.Count == 0 ? "Плана пока нет"
        : $"План: {StructureViewModel.Plural(_work.Chapters.Count, "глава", "главы", "глав")}, {StructureViewModel.Plural(_work.Chapters.Sum(c => c.Sections.Count), "параграф", "параграфа", "параграфов")}";

    /// <summary>Пожелания к работе — сохраняются для вида работы.</summary>
    public string Notes
    {
        get => _notes;
        set
        {
            if (!Set(ref _notes, value ?? "")) return;
            _state().AiNotes = string.IsNullOrWhiteSpace(value) ? null : value;
            _settings.Save();
        }
    }

    /// <summary>Тема, план или вид работы изменились — обновить сведения, список параграфов и черновиков.</summary>
    public void Refresh()
    {
        _work = _context();
        _notes = _state().AiNotes ?? "";
        var selected = _section;
        var drafts = _state().Drafts ?? [];
        Sections.Clear();
        for (var i = 0; i < _work.Chapters.Count; i++)
            for (var j = 0; j < _work.Chapters[i].Sections.Count; j++)
            {
                var title = _work.Chapters[i].Sections[j];
                if (Placeholders.IsPlaceholder(title)) continue;
                Sections.Add(new SectionChoice(i, j, title, $"{i + 1}.{j + 1}. {title}", drafts.ContainsKey(Drafts.Section(title))));
            }
        _section = Sections.FirstOrDefault(s => s.Title == selected?.Title) ?? Sections.FirstOrDefault();
        RefreshDrafts();
        foreach (var name in new[] { nameof(WorkType), nameof(Topic), nameof(HasTopic), nameof(TopicText), nameof(DisciplineText), nameof(PlanText),
                     nameof(Notes), nameof(SelectedSection), nameof(HasSections), nameof(RunHint), nameof(InsertText) })
            OnPropertyChanged(name);
    }

    // ------------------------------------------------------------ задачи

    public static AiTaskCard[] Tasks { get; } =
    [
        new(WritingTask.Plan, "План работы", "", "Главы и параграфы по теме — одним нажатием в структуру"),
        new(WritingTask.Introduction, "Введение", "", "Актуальность, цель, задачи, объект и предмет"),
        new(WritingTask.Section, "Текст параграфа", "", "Черновик выбранного параграфа плана"),
        new(WritingTask.Conclusion, "Заключение", "", "Выводы по главам и итог по цели"),
        new(WritingTask.Improve, "Улучшить текст", "", "Научный стиль, без ошибок и «воды»"),
        new(WritingTask.References, "Список литературы", "", "Оформить ваши источники по ГОСТ Р 7.0.100"),
        new(WritingTask.Question, "Свой вопрос", "", "Спросить что угодно о работе"),
    ];

    public WritingTask Task
    {
        get => _task;
        set
        {
            if (!Set(ref _task, value)) return;
            foreach (var name in new[] { nameof(ShowSectionPicker), nameof(ShowLength), nameof(ShowInput), nameof(InputLabel), nameof(InputPlaceholder),
                         nameof(InputHeight), nameof(RunText), nameof(RunHint), nameof(NeedsTopic) })
                OnPropertyChanged(name);
        }
    }

    public ObservableCollection<SectionChoice> Sections { get; } = new();
    public bool HasSections => Sections.Count > 0;
    public SectionChoice? SelectedSection { get => _section; set { if (Set(ref _section, value)) OnPropertyChanged(nameof(RunHint)); } }

    public static Choice[] Lengths { get; } =
        [new(DraftLength.Short, "≈ 1 страница"), new(DraftLength.Medium, "≈ 2–3 страницы"), new(DraftLength.Long, "≈ 4–5 страниц")];
    public DraftLength Length { get => _length; set => Set(ref _length, value); }

    public bool ShowSectionPicker => _task == WritingTask.Section;
    public bool ShowLength => _task is WritingTask.Introduction or WritingTask.Section or WritingTask.Conclusion;
    public bool ShowInput => _task is WritingTask.Improve or WritingTask.References or WritingTask.Question or WritingTask.Section;
    public double InputHeight => _task == WritingTask.Section ? 64 : 160;
    public string InputLabel => _task switch
    {
        WritingTask.Improve => "Ваш текст",
        WritingTask.References => "Источники — по одному в строке, в любом виде",
        WritingTask.Section => "Что учесть (необязательно)",
        _ => "Вопрос",
    };
    public string InputPlaceholder => _task switch
    {
        WritingTask.Improve => "Вставьте абзац или раздел, который нужно привести в порядок",
        WritingTask.References => "Например: Иванов, Основы менеджмента, Питер 2023 или ссылка на сайт",
        WritingTask.Section => "Например: на примере ООО «Север», с таблицей показателей",
        _ => "Например: какие методы исследования подойдут для моей темы?",
    };
    /// <summary>Тема нужна для плана и черновиков: без неё ИИ не поймёт, о чём писать.</summary>
    public bool NeedsTopic => _task is WritingTask.Plan or WritingTask.Introduction or WritingTask.Section or WritingTask.Conclusion;
    public string RunText => _task switch
    {
        WritingTask.Plan => "Составить план",
        WritingTask.Improve => "Улучшить",
        WritingTask.References => "Оформить",
        WritingTask.Question => "Спросить",
        _ => "Написать черновик",
    };

    /// <summary>Почему кнопка недоступна — или null.</summary>
    public string? RunHint => !IsConnected ? "Сначала подключите ИИ."
        : NeedsTopic && !HasTopic ? "Укажите тему работы на титульном листе — без неё ИИ не поймёт, о чём писать."
        : _task == WritingTask.Section && Sections.Count == 0 ? "В структуре нет параграфов с названиями. Составьте план или назовите параграфы в «Структуре работы»."
        : _task == WritingTask.Conclusion && _work.Chapters.Count == 0 ? "Для заключения нужен план: составьте его или заполните «Структуру работы»."
        : null;

    bool CanRun => !IsRunning && IsConnected && RunHint == null
                   && !(_task is WritingTask.Improve or WritingTask.References or WritingTask.Question && Input.Trim().Length == 0)
                   && !(_task == WritingTask.Section && SelectedSection == null);

    public string Input { get => _input; set => Set(ref _input, value ?? ""); }

    public RelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }

    // ------------------------------------------------------------ запрос и результат

    public bool IsRunning
    {
        get => _running;
        private set { if (Set(ref _running, value)) { OnPropertyChanged(nameof(IsResultReadOnly)); OnPropertyChanged(nameof(ShowResult)); } }
    }
    public string Result { get => _result; set { if (Set(ref _result, value ?? "")) OnResultChanged(); } }
    public bool HasResult => Result.Trim().Length > 0;
    public bool ShowResult => HasResult || IsRunning;
    public bool IsResultReadOnly => IsRunning;
    public string? ResultHeader { get => _resultHeader; private set => Set(ref _resultHeader, value); }
    public string ResultInfo => IsRunning ? "ИИ пишет…" : $"{StructureViewModel.Plural(WritingAssistant.CountWords(Result), "слово", "слова", "слов")} · можно править прямо здесь";
    public string? ErrorTitle { get => _errorTitle; private set => Set(ref _errorTitle, value); }
    public string? ErrorText { get => _errorText; private set { if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => ErrorText != null;

    /// <summary>Результат можно вставить в шаблон: введение, параграф, заключение или список литературы.</summary>
    public bool CanInsert => _resultKey != null && HasResult && !IsRunning;
    public bool ShowInsert => _resultKey != null;
    public bool ShowApplyPlan => _resultTask == WritingTask.Plan && HasResult && _resultKey == null;
    public string InsertText => _resultKey != null && (_state().Drafts?.ContainsKey(_resultKey) ?? false) ? "Обновить в шаблоне" : "Вставить в шаблон";

    public RelayCommand InsertCommand { get; }
    public RelayCommand ApplyPlanCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand ClearResultCommand { get; }

    void OnResultChanged()
    {
        foreach (var name in new[] { nameof(HasResult), nameof(ShowResult), nameof(ResultInfo), nameof(CanInsert), nameof(ShowInsert),
                     nameof(ShowApplyPlan), nameof(InsertText) })
            OnPropertyChanged(name);
    }

    async Task RunAsync()
    {
        if (!CanRun) return;
        if (SavedConnection() is not { } connection)
        {
            // Ключ зашифрован для другого пользователя Windows (настройки перенесли с другого компьютера)
            ErrorTitle = "Ключ не удалось прочитать";
            ErrorText = "Сохранённый ключ недоступен на этом компьютере. Нажмите «Изменить подключение» и вставьте ключ заново.";
            return;
        }
        _work = _context();
        var task = _task;
        var target = task == WritingTask.Section && SelectedSection is { } s ? (s.Chapter, s.Section) : ((int, int)?)null;
        var prompt = WritingAssistant.Build(task, _work with { Notes = Notes }, Input, target, Length);

        _resultTask = task;
        _resultKey = task switch
        {
            WritingTask.Introduction => Drafts.Intro,
            WritingTask.Conclusion => Drafts.Conclusion,
            WritingTask.References => Drafts.References,
            WritingTask.Section when SelectedSection is { } sec => Drafts.Section(sec.Title),
            _ => null,
        };
        ResultHeader = task switch
        {
            WritingTask.Plan => "План работы",
            WritingTask.Introduction => "Черновик введения",
            WritingTask.Conclusion => "Черновик заключения",
            WritingTask.Section => $"Черновик параграфа {SelectedSection?.Label}",
            WritingTask.Improve => "Исправленный текст",
            WritingTask.References => "Список литературы",
            _ => "Ответ",
        };
        ErrorText = null;
        _stopped = false;
        Result = "";
        IsRunning = true;
        OnResultChanged();
        _cts = new CancellationTokenSource();
        var buffer = new System.Text.StringBuilder();
        var sync = SynchronizationContext.Current;
        try
        {
            await AiClient.StreamAsync(connection, prompt.System, prompt.User, piece =>
            {
                lock (buffer) buffer.Append(piece);
                void Show() { lock (buffer) Result = buffer.ToString(); }
                if (sync != null) sync.Post(_ => Show(), null); else Show();
            }, _cts.Token);
            Result = buffer.ToString().Trim();
        }
        catch (OperationCanceledException)
        {
            Result = buffer.ToString().Trim();
            if (!_stopped) { ErrorTitle = "Запрос прерван"; ErrorText = "Ответ не получен до конца."; }
        }
        catch (AiException e)
        {
            Result = buffer.ToString().Trim();
            ErrorTitle = e.Title;
            ErrorText = e.Message;
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            OnResultChanged();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    void Insert()
    {
        if (_resultKey == null || !HasResult) return;
        var state = _state();
        state.Drafts ??= new();
        var replaced = state.Drafts.ContainsKey(_resultKey);
        state.Drafts[_resultKey] = Result.Trim();
        _settings.Save();
        Refresh();
        OnResultChanged();
        var what = DraftTitle(_resultKey).ToLowerInvariant();
        _info(InfoKind.Success, null, replaced ? $"Черновик в шаблоне обновлён: {what}."
            : $"Черновик добавлен: {what}. При создании шаблона он займёт место подсказки.");
    }

    void ApplyPlan()
    {
        var plan = WritingAssistant.ParsePlan(Result);
        if (plan.Count == 0)
        {
            ErrorTitle = "Не получилось разобрать план";
            ErrorText = "Ответ не похож на план «1. Глава / 1.1. Параграф». Поправьте его вручную или составьте заново.";
            return;
        }
        ErrorText = null;
        _applyPlan(plan);
    }

    // ------------------------------------------------------------ черновики в шаблоне

    public ObservableCollection<DraftItem> DraftItems { get; } = new();
    public bool HasDrafts => DraftItems.Count > 0;
    public RelayCommand EditDraftCommand { get; }
    public RelayCommand RemoveDraftCommand { get; }

    void RefreshDrafts()
    {
        DraftItems.Clear();
        var drafts = _state().Drafts ?? [];
        var sectionTitles = _work.Chapters.SelectMany(c => c.Sections).Select(t => t.Trim()).ToHashSet();
        foreach (var (key, text) in drafts.OrderBy(d => d.Key switch { Drafts.Intro => 0, Drafts.Conclusion => 2, Drafts.References => 3, _ => 1 }))
        {
            var orphan = key.StartsWith("section:") && !sectionTitles.Contains(key["section:".Length..]);
            var words = StructureViewModel.Plural(WritingAssistant.CountWords(text), "слово", "слова", "слов");
            DraftItems.Add(new DraftItem(key, DraftTitle(key), orphan ? $"{words} · такого параграфа больше нет в структуре — в шаблон не попадёт" : words, orphan));
        }
        OnPropertyChanged(nameof(HasDrafts));
    }

    static string DraftTitle(string key) => key switch
    {
        Drafts.Intro => "Введение",
        Drafts.Conclusion => "Заключение",
        Drafts.References => "Список литературы",
        _ => "Параграф «" + key["section:".Length..] + "»",
    };

    void EditDraft(DraftItem? item)
    {
        if (item == null || _state().Drafts?.TryGetValue(item.Key, out var text) != true) return;
        _resultKey = item.Key;
        _resultTask = item.Key switch
        {
            Drafts.Intro => WritingTask.Introduction,
            Drafts.Conclusion => WritingTask.Conclusion,
            Drafts.References => WritingTask.References,
            _ => WritingTask.Section,
        };
        ResultHeader = item.Title;
        ErrorText = null;
        Result = text!;
        OnResultChanged();
        ResultShown?.Invoke();
    }

    void RemoveDraft(DraftItem? item)
    {
        if (item == null || _state().Drafts is not { } drafts || !drafts.Remove(item.Key)) return;
        if (drafts.Count == 0) _state().Drafts = null;
        _settings.Save();
        Refresh();
        OnResultChanged();
        _info(InfoKind.Informational, null, $"Черновик удалён: {item.Title.ToLowerInvariant()}. В шаблоне снова будет подсказка.");
    }

    /// <summary>Представление прокручивает к результату.</summary>
    public Action? ResultShown { get; set; }
}
