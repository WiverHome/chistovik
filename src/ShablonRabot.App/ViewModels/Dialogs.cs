using System.Collections.ObjectModel;
using System.IO;
using ShablonRabot.App.Infrastructure;

namespace ShablonRabot.App.ViewModels;

public enum DialogIcon { None, Success, Warning, Error }

public abstract class DialogViewModel : ObservableObject
{
    string _title = "";
    DialogIcon _icon;

    public string Title { get => _title; set => Set(ref _title, value); }
    public DialogIcon Icon { get => _icon; set => Set(ref _icon, value); }
    /// <summary>Esc: закрыть (если разрешено).</summary>
    public virtual bool CanDismiss => true;
    public Action? CloseRequested { get; set; }
    public virtual void Dismiss() => CloseRequested?.Invoke();
}

public sealed class DialogButton
{
    public required string Text { get; init; }
    public required string Result { get; init; }
    public bool IsPrimary { get; init; }
    /// <summary>Получает фокус при открытии (у деструктивных диалогов — «Отмена»).</summary>
    public bool IsDefaultFocus { get; init; }
    public bool IsCancel { get; init; }
}

/// <summary>Диалог с текстом и кнопками: подтверждения, ошибки импорта, дубликаты.</summary>
public sealed class MessageDialogViewModel : DialogViewModel
{
    readonly TaskCompletionSource<string> _tcs = new();

    public string Text { get; init; } = "";
    public IReadOnlyList<DialogButton> Buttons { get; init; } = [];
    public RelayCommand ChooseCommand => new(p => Finish(p as string ?? "cancel"));
    public Task<string> Result => _tcs.Task;

    public override void Dismiss() => Finish(Buttons.FirstOrDefault(b => b.IsCancel)?.Result ?? "cancel");

    void Finish(string result)
    {
        if (_tcs.TrySetResult(result)) CloseRequested?.Invoke();
    }
}

public enum StepState { Pending, Running, Done, Skipped, Failed }

public sealed class GenerationStep(string text) : ObservableObject
{
    string _text = text;
    StepState _state = StepState.Pending;
    public string Text { get => _text; set => Set(ref _text, value); }
    public StepState State { get => _state; set => Set(ref _state, value); }
}

public enum GenerationPhase { Progress, Success, Warning, Locked, NoAccess, Error }

/// <summary>Диалог «Создание шаблона» (DESIGN.md 6.2): этапы, итог и действия с файлом.</summary>
public sealed class GenerationDialogViewModel : DialogViewModel
{
    GenerationPhase _phase;
    string _hint = "Обычно это занимает от 5 до 30 секунд.";
    string _text = "", _details = "", _warningTitle = "", _warningText = "", _warningPdf = "";
    bool _copied;

    public GenerationDialogViewModel(bool pdf)
    {
        Title = "Создание шаблона";
        Steps.Add(new GenerationStep("Собираю документ"));
        Steps.Add(new GenerationStep("Word обновляет содержание и нумерацию"));
        if (pdf) Steps.Add(new GenerationStep("Сохраняю PDF"));
    }

    public ObservableCollection<GenerationStep> Steps { get; } = new();
    public GenerationPhase Phase
    {
        get => _phase;
        set
        {
            if (!Set(ref _phase, value)) return;
            OnPropertyChanged(nameof(IsProgress));
            OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(HasFile));
            OnPropertyChanged(nameof(IsFailure));
        }
    }

    public bool IsProgress => Phase == GenerationPhase.Progress;
    public bool IsDone => Phase is GenerationPhase.Success or GenerationPhase.Warning;
    public bool IsFailure => Phase is GenerationPhase.Locked or GenerationPhase.NoAccess or GenerationPhase.Error;
    public bool HasFile => IsDone;
    public string Hint { get => _hint; set => Set(ref _hint, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string Details { get => _details; set => Set(ref _details, value); }
    public bool HasDetails => Details.Length > 0;
    public string WarningTitle { get => _warningTitle; set => Set(ref _warningTitle, value); }
    public string WarningText { get => _warningText; set => Set(ref _warningText, value); }
    public string WarningPdf { get => _warningPdf; set => Set(ref _warningPdf, value); }
    public bool Copied { get => _copied; set => Set(ref _copied, value); }

    public string DocxName { get; set; } = "";
    public string DocxPath { get; set; } = "";
    public string Folder { get; set; } = "";
    public string? PdfName { get; set; }
    public string? PdfPath { get; set; }
    public bool OpenedAutomatically { get; set; }

    public override bool CanDismiss => true;

    /// <summary>Esc: во время сборки — «Отмена», после — «Закрыть».</summary>
    public override void Dismiss()
    {
        if (IsProgress) { CancelCommand?.Execute(null); return; }
        base.Dismiss();
    }

    public RelayCommand? OpenCommand { get; set; }
    public RelayCommand? ShowInFolderCommand { get; set; }
    public RelayCommand? CloseCommand { get; set; }
    public RelayCommand? RetryCommand { get; set; }
    public RelayCommand? SaveAsCommand { get; set; }
    public RelayCommand? CancelCommand { get; set; }
    public RelayCommand? CopyDetailsCommand { get; set; }
    public RelayCommand? OpenPdfCommand { get; set; }

    public void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed class SaveProfileDialogViewModel : DialogViewModel, System.ComponentModel.INotifyDataErrorInfo
{
    string _name;
    bool _includeOrg, _touched;

    public SaveProfileDialogViewModel(string defaultName)
    {
        Title = "Сохранить профиль";
        _name = defaultName;
    }

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { _touched = true; RaiseError(); } }
    }

    public bool IncludeOrg { get => _includeOrg; set => Set(ref _includeOrg, value); }
    public string? Error => _touched && string.IsNullOrWhiteSpace(Name) ? "Введите название профиля" : null;
    public RelayCommand? SaveCommand { get; set; }
    public RelayCommand CancelCommand => new(Dismiss);

    public bool Validate()
    {
        _touched = true;
        RaiseError();
        return Error == null;
    }

    void RaiseError()
    {
        OnPropertyChanged(nameof(Error));
        ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(nameof(Name)));
    }

    public bool HasErrors => Error != null;
    public event EventHandler<System.ComponentModel.DataErrorsChangedEventArgs>? ErrorsChanged;
    public System.Collections.IEnumerable GetErrors(string? propertyName) =>
        propertyName == nameof(Name) && Error != null ? new[] { Error } : Array.Empty<string>();
}

public sealed class ShareDialogViewModel : DialogViewModel
{
    bool _includeOrg, _copied;

    public ShareDialogViewModel(string fileName)
    {
        Title = "Поделиться профилем";
        FileName = fileName;
    }

    public string FileName { get; }
    public string SizeText { get; set; } = "";
    public bool IncludeOrg
    {
        get => _includeOrg;
        set { if (Set(ref _includeOrg, value)) { Copied = false; IncludeOrgChanged?.Invoke(); } }
    }
    public bool Copied { get => _copied; set => Set(ref _copied, value); }
    public Action? IncludeOrgChanged { get; set; }
    public RelayCommand? CopyCommand { get; set; }
    public RelayCommand? SaveAsCommand { get; set; }
    public RelayCommand CloseCommand => new(Dismiss);

    public static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} Б" : $"{bytes / 1024.0:0.#} КБ";
}

/// <summary>Увеличенный предпросмотр: тот же лист, крупнее.</summary>
public sealed class ZoomDialogViewModel : DialogViewModel
{
    public ZoomDialogViewModel(MainViewModel main)
    {
        Title = "Предпросмотр";
        Main = main;
    }

    public MainViewModel Main { get; }
    public RelayCommand CloseCommand => new(Dismiss);
}

public static class PathText
{
    /// <summary>Обрезка середины длинного пути: C:\Users\…\Учёба</summary>
    public static string Middle(string path, int max = 48)
    {
        if (path.Length <= max) return path;
        var head = path[..(max / 2 - 1)];
        var tail = path[^(max / 2 - 1)..];
        return head + "…" + tail;
    }

    public static string FolderOf(string path) => Path.GetDirectoryName(path) ?? "";
}
