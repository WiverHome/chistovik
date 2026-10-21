using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ShablonRabot.App.Infrastructure;
using ShablonRabot.Core;

namespace ShablonRabot.App.ViewModels;

public abstract class OutlineItem : ObservableObject, INotifyDataErrorInfo
{
    string _title = "";
    bool _wantsFocus;

    public string Title
    {
        get => _title;
        set
        {
            if (!Set(ref _title, value ?? "")) return;
            OnPropertyChanged(nameof(HasErrors));
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Title)));
            OnPropertyChanged(nameof(Error));
            Owner.RaiseChanged();
        }
    }

    public abstract StructureViewModel Owner { get; }
    public abstract string Number { get; }
    public abstract string Placeholder { get; }
    /// <summary>Подсказка из профиля для пустого названия, например «[Название первой главы (теоретической)]».</summary>
    public string? ProfileHint { get; init; }
    protected string HintOr(string fallback) => ProfileHint != null ? ProfileHint.Trim('[', ']') : fallback;
    public abstract string AutomationName { get; }
    /// <summary>Представление переводит фокус в поле названия, как только строка появится.</summary>
    public bool WantsFocus { get => _wantsFocus; set => Set(ref _wantsFocus, value); }
    public string? Error => Title.Length > 300 ? "Слишком длинный текст — не больше 300 символов" : null;
    public bool HasErrors => Error != null;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName) => Error != null ? new[] { Error } : Array.Empty<string>();

    internal void Renumber()
    {
        OnPropertyChanged(nameof(Number));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(Placeholder));
    }
}

public sealed class SectionViewModel : OutlineItem
{
    public SectionViewModel(ChapterViewModel chapter, string title)
    {
        Chapter = chapter;
        Title = title;
    }

    public ChapterViewModel Chapter { get; internal set; }
    public override StructureViewModel Owner => Chapter.Owner;
    public override string Number => $"{Chapter.Number}.{Chapter.Sections.IndexOf(this) + 1}";
    public override string Placeholder => HintOr("Название параграфа");
    public override string AutomationName => $"Параграф {Number}: {Title}";
}

public sealed class ChapterViewModel : OutlineItem
{
    public ChapterViewModel(StructureViewModel owner, string title, IEnumerable<string> sections)
    {
        Owner = owner;
        Title = Clean(title, out var hint);
        ProfileHint = hint;
        foreach (var s in sections) Sections.Add(new SectionViewModel(this, Clean(s, out var h)) { ProfileHint = h });
        Sections.CollectionChanged += (_, _) =>
        {
            foreach (var s in Sections) s.Renumber();
            OnPropertyChanged(nameof(CanAddSection));
            OnPropertyChanged(nameof(AddSectionTip));
            owner.RaiseChanged();
        };
    }

    public override StructureViewModel Owner { get; }

    /// <summary>Подсказки в [скобках] из профиля показываем пустым полем с этой подсказкой.</summary>
    static string Clean(string title, out string? hint)
    {
        var isHint = Placeholders.IsPlaceholder(title) && title.Trim().Length > 0;
        hint = isHint ? title.Trim() : null;
        // «[Название параграфа 1.1]» — не смысловая подсказка: нумерация пересчитывается заново
        if (hint != null && System.Text.RegularExpressions.Regex.IsMatch(hint, @"^\[Название (главы|параграфа)( [\d.]+)?\]$")) hint = null;
        return isHint ? "" : title;
    }

    public ObservableCollection<SectionViewModel> Sections { get; } = new();
    public override string Number => (Owner.Chapters.IndexOf(this) + 1).ToString();
    public override string Placeholder => HintOr("Название главы");
    public override string AutomationName => $"Глава {Number}: {Title}";
    public bool CanAddSection => Sections.Count < StructurePlan.MaxSections;
    public string AddSectionTip => CanAddSection ? $"Добавить параграф в главу {Number}" : "Не больше 10 параграфов в главе";

    internal void RenumberAll()
    {
        Renumber();
        OnPropertyChanged(nameof(AddSectionTip));
        foreach (var s in Sections) s.Renumber();
    }
}

/// <summary>Структура работы (DESIGN.md 4.4).</summary>
public sealed class StructureViewModel : ObservableObject
{
    bool _toc, _introduction, _conclusion, _references, _appendices, _examples, _loading;

    public StructureViewModel()
    {
        Chapters.CollectionChanged += (_, _) =>
        {
            foreach (var c in Chapters) c.RenumberAll();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CanAddChapter));
            OnPropertyChanged(nameof(AddChapterTip));
            RaiseChanged();
        };
        AppendixCount.Changed += (_, _) => RaiseChanged();
        AppendixCount.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NumberRule.Error)) OnPropertyChanged(nameof(ErrorCount)); };

        AddChapterCommand = new RelayCommand(() => AddChapter(Chapters.Count), () => CanAddChapter);
        AddSectionCommand = new RelayCommand(p => { if (p is ChapterViewModel c) AddSection(c, c.Sections.Count); },
            p => p is ChapterViewModel { CanAddSection: true });
        AddSectionAfterCommand = new RelayCommand(p => AddAfter(p as OutlineItem));
        DeleteCommand = new RelayCommand(p => Delete(p as OutlineItem));
        MoveUpCommand = new RelayCommand(p => Move(p as OutlineItem, -1), p => CanMove(p as OutlineItem, -1));
        MoveDownCommand = new RelayCommand(p => Move(p as OutlineItem, 1), p => CanMove(p as OutlineItem, 1));
        IndentCommand = new RelayCommand(p => Indent(p as ChapterViewModel), p => p is ChapterViewModel c && Chapters.IndexOf(c) > 0);
        OutdentCommand = new RelayCommand(p => Outdent(p as SectionViewModel), p => p is SectionViewModel && CanAddChapter);
    }

    /// <summary>Удаление: текст для инфобара и действие «Отменить».</summary>
    public event Action<string, Action>? Deleted;
    /// <summary>Перевести фокус в поле названия уже показанной строки.</summary>
    public event Action<OutlineItem>? FocusItem;
    /// <summary>Ограничение не дало выполнить действие (текст для инфобара).</summary>
    public event Action<string>? Limited;
    public event EventHandler? Changed;

    public ObservableCollection<ChapterViewModel> Chapters { get; } = new();
    public NumberRule AppendixCount { get; } = new(1, 10, true, "Введите число от 1 до 10");

    /// <summary>Содержание можно отключить у любого вида работы — решает методичка (аудит 1.1 №12).</summary>
    public bool Toc { get => _toc; set => SetPart(ref _toc, value); }
    public bool Introduction { get => _introduction; set => SetPart(ref _introduction, value); }
    public bool Conclusion { get => _conclusion; set => SetPart(ref _conclusion, value); }
    public bool References { get => _references; set => SetPart(ref _references, value); }
    public bool Appendices { get => _appendices; set => SetPart(ref _appendices, value); }
    public bool Examples { get => _examples; set => SetPart(ref _examples, value); }

    public bool IsEmpty => Chapters.Count == 0;
    public bool CanAddChapter => Chapters.Count < StructurePlan.MaxChapters;
    public string AddChapterTip => CanAddChapter ? "Добавить главу" : "Не больше 10 глав";
    public int ErrorCount => (Appendices && AppendixCount.HasErrors ? 1 : 0) + Chapters.Sum(c => (c.HasErrors ? 1 : 0) + c.Sections.Count(s => s.HasErrors));
    int AppendixTotal => Appendices ? (int)AppendixCount.Value : 0;

    public string AppendixLetters
    {
        get
        {
            var n = Math.Clamp(AppendixTotal, 0, StructurePlan.MaxAppendices);
            return n switch
            {
                0 => "",
                1 => "Приложение А",
                _ => "Приложения " + string.Join(", ", StructurePlan.AppendixLetters.Take(n)),
            };
        }
    }

    public string Summary
    {
        get
        {
            if (Chapters.Count == 0) return "Без глав";
            var sections = Chapters.Sum(c => c.Sections.Count);
            return $"{Plural(Chapters.Count, "глава", "главы", "глав")} · {Plural(sections, "параграф", "параграфа", "параграфов")}";
        }
    }

    public RelayCommand AddChapterCommand { get; }
    public RelayCommand AddSectionCommand { get; }
    public RelayCommand AddSectionAfterCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand IndentCommand { get; }
    public RelayCommand OutdentCommand { get; }

    // ------------------------------------------------------------ загрузка и применение

    public void Load(Profile profile)
    {
        _loading = true;
        var plan = StructurePlan.From(profile);
        Toc = plan.Toc;
        Introduction = plan.Introduction;
        Conclusion = plan.Conclusion;
        References = plan.References;
        Appendices = plan.Appendices > 0;
        AppendixCount.Load(Math.Max(plan.Appendices, 1));
        Examples = plan.Examples;
        Chapters.Clear();
        foreach (var c in plan.Chapters) Chapters.Add(new ChapterViewModel(this, c.Title, c.Sections));
        _loading = false;
        RaiseChanged();
    }

    public void ApplyTo(Profile profile, Profile template)
    {
        new StructurePlan
        {
            Toc = Toc, Introduction = Introduction, Conclusion = Conclusion, References = References,
            Appendices = AppendixTotal, Examples = Examples,
            Chapters = Chapters.Select(c => new ChapterPlan(
                Title(c.Title, c.ProfileHint ?? $"[Название главы {c.Number}]"),
                c.Sections.Select(s => Title(s.Title, s.ProfileHint ?? $"[Название параграфа {s.Number}]")).ToList())).ToList(),
        }.ApplyTo(profile, template);
    }

    static string Title(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    internal void RaiseChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AppendixLetters));
        OnPropertyChanged(nameof(ErrorCount));
        if (!_loading) Changed?.Invoke(this, EventArgs.Empty);
    }

    void SetPart(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name)) RaiseChanged();
    }

    // ------------------------------------------------------------ редактирование

    void AddChapter(int index)
    {
        if (!CanAddChapter) return;
        var chapter = new ChapterViewModel(this, "", []) { WantsFocus = true };
        Chapters.Insert(index, chapter);
    }

    void AddSection(ChapterViewModel chapter, int index)
    {
        if (!chapter.CanAddSection) return;
        chapter.Sections.Insert(index, new SectionViewModel(chapter, "") { WantsFocus = true });
    }

    /// <summary>Enter в названии: новый элемент того же уровня ниже.</summary>
    void AddAfter(OutlineItem? item)
    {
        switch (item)
        {
            case ChapterViewModel c: AddChapter(Chapters.IndexOf(c) + 1); break;
            case SectionViewModel s: AddSection(s.Chapter, s.Chapter.Sections.IndexOf(s) + 1); break;
        }
    }

    void Delete(OutlineItem? item)
    {
        switch (item)
        {
            case ChapterViewModel c:
            {
                var index = Chapters.IndexOf(c);
                OutlineItem? neighbor = index + 1 < Chapters.Count ? Chapters[index + 1] : index > 0 ? Chapters[index - 1] : null;
                Chapters.Remove(c);
                if (neighbor != null) FocusItem?.Invoke(neighbor);
                var text = string.IsNullOrWhiteSpace(c.Title)
                    ? $"Глава {index + 1} удалена."
                    : $"Глава «{c.Title}» удалена вместе с параграфами ({c.Sections.Count}).";
                Deleted?.Invoke(text, () => { c.WantsFocus = true; Chapters.Insert(Math.Min(index, Chapters.Count), c); });
                break;
            }
            case SectionViewModel s:
            {
                var chapter = s.Chapter;
                var number = s.Number;
                var index = chapter.Sections.IndexOf(s);
                OutlineItem neighbor = index + 1 < chapter.Sections.Count ? chapter.Sections[index + 1]
                    : index > 0 ? chapter.Sections[index - 1] : chapter;
                chapter.Sections.Remove(s);
                FocusItem?.Invoke(neighbor);
                Deleted?.Invoke($"Параграф {number} удалён.", () => { s.WantsFocus = true; chapter.Sections.Insert(Math.Min(index, chapter.Sections.Count), s); });
                break;
            }
        }
    }

    bool CanMove(OutlineItem? item, int delta) => item switch
    {
        ChapterViewModel c => Chapters.IndexOf(c) + delta is var i && i >= 0 && i < Chapters.Count,
        SectionViewModel s => s.Chapter.Sections.IndexOf(s) + delta is var j && j >= 0 && j < s.Chapter.Sections.Count
                              || (delta < 0 ? Chapters.IndexOf(s.Chapter) > 0 : Chapters.IndexOf(s.Chapter) < Chapters.Count - 1),
        _ => false,
    };

    /// <summary>Перемещение; параграф на краю главы переходит в соседнюю главу.</summary>
    void Move(OutlineItem? item, int delta)
    {
        switch (item)
        {
            case ChapterViewModel c:
            {
                var i = Chapters.IndexOf(c);
                if (i + delta < 0 || i + delta >= Chapters.Count) return;
                c.WantsFocus = true;
                Chapters.Move(i, i + delta);
                break;
            }
            case SectionViewModel s:
            {
                var list = s.Chapter.Sections;
                var i = list.IndexOf(s);
                s.WantsFocus = true;
                if (i + delta >= 0 && i + delta < list.Count) { list.Move(i, i + delta); return; }
                var ci = Chapters.IndexOf(s.Chapter) + delta;
                if (ci < 0 || ci >= Chapters.Count || !Chapters[ci].CanAddSection) { s.WantsFocus = false; return; }
                list.Remove(s);
                var target = Chapters[ci];
                s.Chapter = target;
                target.Sections.Insert(delta < 0 ? target.Sections.Count : 0, s);
                break;
            }
        }
    }

    /// <summary>Можно ли бросить элемент на строку: глава — на главу, параграф — на параграф или главу.</summary>
    public static bool CanDrop(OutlineItem source, OutlineItem target) =>
        source != target && (source, target) switch
        {
            (ChapterViewModel, ChapterViewModel) => true,
            (SectionViewModel s, SectionViewModel t) => s.Chapter == t.Chapter || t.Chapter.CanAddSection,
            (SectionViewModel s, ChapterViewModel c) => s.Chapter == c || c.CanAddSection,
            _ => false,
        };

    /// <summary>Перетаскивание: элемент встаёт на место строки, на которую его бросили.</summary>
    public void DropOnto(OutlineItem source, OutlineItem target)
    {
        if (!CanDrop(source, target)) return;
        source.WantsFocus = true;
        switch (source, target)
        {
            case (ChapterViewModel c, ChapterViewModel t):
                Chapters.Move(Chapters.IndexOf(c), Chapters.IndexOf(t));
                break;
            case (SectionViewModel s, SectionViewModel t) when s.Chapter == t.Chapter:
                s.Chapter.Sections.Move(s.Chapter.Sections.IndexOf(s), t.Chapter.Sections.IndexOf(t));
                break;
            case (SectionViewModel s, SectionViewModel t):
                s.Chapter.Sections.Remove(s);
                s.Chapter = t.Chapter;
                t.Chapter.Sections.Insert(t.Chapter.Sections.IndexOf(t), s);
                break;
            case (SectionViewModel s, ChapterViewModel c):
                s.Chapter.Sections.Remove(s);
                s.Chapter = c;
                c.Sections.Insert(0, s);
                break;
        }
    }

    /// <summary>Alt+→: глава становится последним параграфом предыдущей главы.</summary>
    void Indent(ChapterViewModel? c)
    {
        if (c == null) return;
        var i = Chapters.IndexOf(c);
        if (i <= 0 || !Chapters[i - 1].CanAddSection) return;
        var prev = Chapters[i - 1];
        if (prev.Sections.Count + 1 + c.Sections.Count > StructurePlan.MaxSections)
        {
            Limited?.Invoke("Не больше 10 параграфов в главе: глава не стала параграфом.");
            return;
        }
        Chapters.RemoveAt(i);
        prev.Sections.Add(new SectionViewModel(prev, c.Title) { WantsFocus = true });
        foreach (var s in c.Sections.ToList())
        {
            if (!prev.CanAddSection) break;
            s.Chapter = prev;
            prev.Sections.Add(s);
        }
    }

    /// <summary>Alt+←: параграф становится главой сразу после своей главы.</summary>
    void Outdent(SectionViewModel? s)
    {
        if (s == null || !CanAddChapter) return;
        var chapter = s.Chapter;
        var i = chapter.Sections.IndexOf(s);
        var following = chapter.Sections.Skip(i + 1).Select(x => x.Title).ToList();
        while (chapter.Sections.Count > i) chapter.Sections.RemoveAt(i);
        Chapters.Insert(Chapters.IndexOf(chapter) + 1, new ChapterViewModel(this, s.Title, following) { WantsFocus = true });
    }

    public static string Plural(int n, string one, string few, string many)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        var word = mod10 == 1 && mod100 != 11 ? one : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? few : many;
        return $"{n} {word}";
    }
}
