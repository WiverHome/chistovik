using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using ShablonRabot.App.Infrastructure;
using ShablonRabot.Core;

namespace ShablonRabot.App.ViewModels;

public enum FieldKind { Text, Multiline, Digits, Combo }

/// <summary>Поле титульного листа (DESIGN.md 4.2, 13.4).</summary>
public sealed partial class FieldViewModel : ObservableObject, INotifyDataErrorInfo
{
    [GeneratedRegex(@"^\s*([А-ЯЁA-Z][а-яёa-z\-]+)\s+([А-ЯЁA-Z])[а-яёa-z]+\s+([А-ЯЁA-Z])[а-яёa-z]+\s*$")]
    private static partial Regex FullName();

    static readonly HashSet<string> NameKeys = ["student", "supervisor", "head", "normcontrol", "org_supervisor"];
    static readonly HashSet<string> QuoteKeys = ["topic", "university", "discipline"];

    string _value = "";
    string? _error, _tip, _tipFix;
    string? _fixedValue;

    public FieldViewModel(string key, string label, string placeholder, string? help, FieldKind kind, string defaultValue, bool counted)
    {
        Key = key;
        Label = label;
        Placeholder = placeholder;
        Help = help;
        Kind = kind;
        DefaultValue = defaultValue;
        IsCounted = counted;
        FixCommand = new RelayCommand(() => { if (_fixedValue != null) Value = _fixedValue; });
    }

    /// <summary>Предлагать «И. О. Фамилия» вместо полного ФИО (кроме видов работ, где нужно полное).</summary>
    public bool SuggestInitials { get; init; } = true;
    public string Key { get; }
    public string Label { get; }
    public string Placeholder { get; }
    public string? Help { get; }
    public FieldKind Kind { get; }
    /// <summary>Значение по умолчанию из профиля (не подсказка): министерство, «КУРСОВАЯ РАБОТА»…</summary>
    public string DefaultValue { get; }
    public bool IsCounted { get; }
    public bool IsHalf { get; set; }
    public bool IsMultiline => Kind == FieldKind.Multiline;
    public bool IsDigits => Kind == FieldKind.Digits;
    public bool IsCombo => Kind == FieldKind.Combo;
    public bool IsPlainText => Kind is FieldKind.Text or FieldKind.Digits;
    public int MaxLength => 0;
    public IReadOnlyList<string> Options { get; init; } = [];

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value ?? "")) return;
            Validate();
            OnPropertyChanged(nameof(IsFilled));
            OnPropertyChanged(nameof(IsDefault));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsFilled => !string.IsNullOrWhiteSpace(Value);
    public bool IsDefault => Value.Trim() == DefaultValue;
    public string? Error => _error;
    public string? Tip => _tip;
    public string? TipFix => _tipFix;
    /// <summary>Под полем: ошибка, иначе подсказка.</summary>
    public string? HelpOrError => _error ?? Help;
    public RelayCommand FixCommand { get; }
    public bool HasErrors => _error != null;
    public event EventHandler? Changed;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName) =>
        propertyName == nameof(Value) && _error != null ? new[] { _error } : Array.Empty<string>();

    void Validate()
    {
        var v = Value.Trim();
        string? error = null;
        if (v.Length > 300) error = "Слишком длинный текст — не больше 300 символов";
        else if (Key == "year" && v.Length > 0 && !(v.Length == 4 && int.TryParse(v, out var y) && y is >= 1990 and <= 2100))
            error = "Год — четыре цифры, например 2026";
        else if (Key == "course" && v.Length > 0 && !(int.TryParse(v, out var c) && c is >= 1 and <= 6))
            error = "Курс — число от 1 до 6";

        if (error != _error)
        {
            _error = error;
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Value)));
            OnPropertyChanged(nameof(Error));
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(HelpOrError));
        }

        // Умные подсказки (не ошибки)
        _tip = _tipFix = _fixedValue = null;
        if (SuggestInitials && NameKeys.Contains(Key) && FullName().Match(v) is { Success: true } m)
        {
            _fixedValue = $"{m.Groups[2].Value}. {m.Groups[3].Value}. {m.Groups[1].Value}";
            _tip = $"На титульном листе обычно пишут «{_fixedValue}».";
            _tipFix = "Исправить";
        }
        else if (QuoteKeys.Contains(Key) && v.Length >= 2 && ((v[0] == '«' && v[^1] == '»') || (v[0] == '"' && v[^1] == '"')))
        {
            _fixedValue = v[1..^1].Trim();
            _tip = "Кавычки уже есть в шаблоне.";
            _tipFix = "Убрать кавычки";
        }
        OnPropertyChanged(nameof(Tip));
        OnPropertyChanged(nameof(TipFix));
    }
}

public sealed class FieldGroupViewModel : ObservableObject
{
    public FieldGroupViewModel(string title) => Title = title;

    public string Title { get; }
    public ObservableCollection<FieldViewModel> Fields { get; } = new();
    /// <summary>Свёрнутый блок «Шапка» (министерство, тип учреждения) — только у «Учебного заведения».</summary>
    public ObservableCollection<FieldViewModel> HeaderFields { get; } = new();
    public bool HasHeader => HeaderFields.Count > 0;
    public bool HeaderChanged => HeaderFields.Any(f => !f.IsDefault);
    public string HeaderState => HeaderChanged ? "Изменено" : "По умолчанию";
    public RelayCommand ResetHeaderCommand => new(() => { foreach (var f in HeaderFields) f.Value = f.DefaultValue; });

    public string Counter
    {
        get
        {
            var counted = Fields.Where(f => f.IsCounted).ToList();
            return counted.Count == 0 ? "" : $"{counted.Count(f => f.IsFilled)} из {counted.Count}";
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Counter));
        OnPropertyChanged(nameof(HeaderChanged));
        OnPropertyChanged(nameof(HeaderState));
    }
}

/// <summary>Поля титульного листа: набор зависит от профиля (только поля, которые есть на титуле).</summary>
public sealed partial class FieldsViewModel : ObservableObject
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex FieldRef();

    [GeneratedRegex(@"[\[\]]")]
    private static partial Regex Brackets();

    sealed record Meta(string Group, string Label, string Placeholder, string? Help, FieldKind Kind = FieldKind.Text, bool Half = false);

    static readonly Dictionary<string, Meta> Catalog = new()
    {
        ["university"] = new("Учебное заведение", "Университет", "Полное наименование университета", "Без кавычек — «ёлочки» добавятся сами."),
        ["institute"] = new("Учебное заведение", "Институт или факультет", "Например: Институт информационных технологий", null),
        ["department"] = new("Учебное заведение", "Кафедра", "Например: информационных систем", "Слово «Кафедра» уже есть в шаблоне.", Half: true),
        ["city"] = new("Учебное заведение", "Город", "Например: Казань", null, Half: true),
        ["ministry"] = new("Шапка", "Министерство", "Министерство науки и высшего образования…", null, FieldKind.Multiline),
        ["org_form"] = new("Шапка", "Тип учреждения", "Например: Федеральное государственное бюджетное…", null, FieldKind.Multiline),
        ["work_type"] = new("Работа", "Вид работы на титульном листе", "Например: КУРСОВАЯ РАБОТА", "Так и будет написано на титульном листе."),
        ["work_kind"] = new("Работа", "Уточнение вида работы", "Например: (бакалаврская работа)", null, FieldKind.Combo),
        ["project_kind"] = new("Работа", "К какой работе записка", "Например: к курсовому проекту", null, FieldKind.Combo),
        ["discipline"] = new("Работа", "Дисциплина", "Название дисциплины", "Без кавычек."),
        ["topic"] = new("Работа", "Тема работы", "Тема без кавычек — как в задании или приказе", "Кавычки «» добавятся сами.", FieldKind.Multiline),
        ["specialty"] = new("Работа", "Направление подготовки", "Например: 09.03.02 Информационные системы и технологии", "Код и название."),
        ["profile"] = new("Работа", "Направленность (профиль)", "Например: Разработка программного обеспечения", null),
        ["designation"] = new("Работа", "Обозначение документа", "Например: КП.09.03.01.07.000 ПЗ", "Код по правилам кафедры; «ПЗ» — пояснительная записка."),
        ["variant"] = new("Работа", "Вариант", "Например: 7", null, Half: true),
        ["practice_kind"] = new("Работа", "Вид практики", "Например: о прохождении производственной практики", "Как в приказе о практике.", FieldKind.Combo),
        ["organization"] = new("Работа", "Место практики", "Например: ООО «Ромашка», отдел разработки", null),
        ["period"] = new("Работа", "Сроки практики", "Например: с 01.07.2026 по 28.07.2026", null),
        ["degree"] = new("Работа", "Учёная степень", "Например: на соискание учёной степени кандидата технических наук", null, FieldKind.Combo),
        ["year"] = new("Работа", "Год", "", "Пусто — подставится текущий год.", FieldKind.Text, Half: true),
        ["student"] = new("Студент", "ФИО студента", "И. О. Фамилия", "Инициалы перед фамилией: И. И. Иванов."),
        ["course"] = new("Студент", "Курс", "Например: 3", null, FieldKind.Text, Half: true),
        ["group"] = new("Студент", "Группа", "Например: ИС-31", null, Half: true),
        ["supervisor"] = new("Руководство", "Руководитель", "И. О. Фамилия", null, Half: true),
        ["supervisor_position"] = new("Руководство", "Должность и степень руководителя", "Например: доцент, канд. техн. наук", null, Half: true),
        ["head"] = new("Руководство", "Заведующий кафедрой", "И. О. Фамилия", null, Half: true),
        ["head_position"] = new("Руководство", "Должность и степень заведующего", "Например: профессор, д-р экон. наук", null, Half: true),
        ["normcontrol"] = new("Руководство", "Нормоконтролёр", "И. О. Фамилия", null, Half: true),
        ["org_supervisor"] = new("Руководство", "Руководитель от организации", "И. О. Фамилия", null, Half: true),
        ["org_supervisor_position"] = new("Руководство", "Должность в организации", "Например: начальник отдела", null, Half: true),
    };

    static readonly string[] GroupOrder = ["Учебное заведение", "Работа", "Студент", "Руководство", "Другое"];
    static readonly string[] WorkKinds = ["(бакалаврская работа)", "(магистерская диссертация)", "(дипломная работа)", "(дипломный проект)"];
    static readonly string[] ProjectKinds = ["к курсовому проекту", "к курсовой работе", "к выпускной квалификационной работе", "к дипломному проекту"];
    static readonly string[] PracticeKinds =
    [
        "о прохождении учебной практики", "о прохождении производственной практики",
        "о прохождении преддипломной практики", "о прохождении научно-исследовательской работы",
    ];
    static readonly string[] Degrees =
    [
        "на соискание учёной степени кандидата технических наук", "на соискание учёной степени кандидата экономических наук",
        "на соискание учёной степени кандидата педагогических наук", "на соискание учёной степени доктора технических наук",
    ];

    /// <summary>Поля, которые зависят от профиля и хранятся отдельно для каждого вида работы.</summary>
    public static readonly HashSet<string> ProfileSpecific = ["work_type", "work_kind", "project_kind", "practice_kind", "degree", "ministry", "org_form"];

    public ObservableCollection<FieldGroupViewModel> Groups { get; } = new();
    public IEnumerable<FieldViewModel> All => Groups.SelectMany(g => g.Fields.Concat(g.HeaderFields));
    public event EventHandler? Changed;

    public int FilledCount => All.Count(f => f.IsCounted && f.IsFilled);
    public int TotalCount => All.Count(f => f.IsCounted);
    public int ErrorCount => All.Count(f => f.HasErrors);
    public bool HasAnyValue => All.Any(f => f.IsFilled && !f.IsDefault);

    public string NavSubtitle => ErrorCount > 0 ? $"Ошибок: {ErrorCount}"
        : FilledCount == TotalCount ? "Всё заполнено" : $"Заполнено {FilledCount} из {TotalCount}";

    public FieldViewModel? Find(string key) => All.FirstOrDefault(f => f.Key == key);

    public void Load(Profile profile, IReadOnlyDictionary<string, string> common, IReadOnlyDictionary<string, string>? specific)
    {
        Groups.Clear();
        var used = TitleKeys(profile);
        var groups = GroupOrder.ToDictionary(g => g, g => new FieldGroupViewModel(g));
        var hasCourse = used.Contains("course");

        var order = Catalog.Keys.ToList();
        var ordered = profile.VisibleFields.Where(f => used.Contains(f.Key))
            .OrderBy(f => order.IndexOf(f.Key) is var i && i >= 0 ? i : int.MaxValue);
        foreach (var (key, profileValue) in ordered)
        {
            var meta = Catalog.GetValueOrDefault(key) ?? new Meta("Другое", key, "", null);
            // Вид работы может уточнить подпись поля («ФИО автора» в диссертации)
            var hint = profile.FieldHints?.GetValueOrDefault(key);
            if (hint != null) meta = meta with { Label = hint.Label ?? meta.Label, Placeholder = hint.Placeholder ?? meta.Placeholder, Help = hint.Help ?? meta.Help };
            var isHint = Placeholders.IsPlaceholder(profileValue);
            // Для известных полей — тексты из дизайна; подсказка профиля — только для полей «Другое»
            var placeholder = key == "year" ? DateTime.Today.Year.ToString()
                : Catalog.ContainsKey(key) || hint?.Placeholder != null ? meta.Placeholder
                : Brackets().Replace(profileValue, "").Trim();
            var field = new FieldViewModel(key, meta.Label, placeholder, meta.Help, meta.Kind, isHint ? "" : profileValue,
                counted: isHint && key != "year")
            {
                SuggestInitials = hint?.Initials != false,
                IsHalf = key == "student" ? !hasCourse : meta.Half,
                Options = key switch { "work_kind" => WorkKinds, "project_kind" => ProjectKinds, "practice_kind" => PracticeKinds, "degree" => Degrees, _ => [] },
            };
            var saved = (ProfileSpecific.Contains(key) ? specific : common)?.GetValueOrDefault(key);
            field.Value = saved ?? (isHint ? "" : profileValue);
            field.Changed += (_, _) => RaiseChanged();

            if (meta.Group == "Шапка") groups["Учебное заведение"].HeaderFields.Add(field);
            else groups[meta.Group].Fields.Add(field);
        }
        foreach (var g in GroupOrder)
            if (groups[g].Fields.Count > 0 || groups[g].HasHeader) Groups.Add(groups[g]);
        RaiseChanged();
    }

    /// <summary>Ключи полей, которые реально встречаются на титульном листе профиля.</summary>
    static HashSet<string> TitleKeys(Profile p)
    {
        var texts = p.TitlePage.Blocks.SelectMany(b => b.Lines.Concat(b.Rows.SelectMany(r => r)))
            .Concat(p.TitlePage.BottomLines);
        return texts.SelectMany(t => FieldRef().Matches(t).Select(m => m.Groups[1].Value)).ToHashSet();
    }

    public void ApplyTo(Profile profile)
    {
        foreach (var f in All)
            if (f.IsFilled) profile.Fields[f.Key] = f.Value.Trim();
            else if (f.Key == "year") profile.Fields[f.Key] = "";
            else if (f.DefaultValue.Length > 0) profile.Fields[f.Key] = f.DefaultValue;
    }

    public void Remember(IDictionary<string, string> common, IDictionary<string, string> specific)
    {
        foreach (var f in All)
        {
            var store = ProfileSpecific.Contains(f.Key) ? specific : common;
            store[f.Key] = f.Value.Trim();
        }
    }

    public Dictionary<string, string> Snapshot() => All.ToDictionary(f => f.Key, f => f.Value);

    public void Restore(Dictionary<string, string> snapshot)
    {
        foreach (var f in All) if (snapshot.TryGetValue(f.Key, out var v)) f.Value = v;
    }

    public void Clear()
    {
        foreach (var f in All) f.Value = f.DefaultValue;
    }

    void RaiseChanged()
    {
        foreach (var g in Groups) g.Refresh();
        OnPropertyChanged(nameof(FilledCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(NavSubtitle));
        OnPropertyChanged(nameof(HasAnyValue));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
