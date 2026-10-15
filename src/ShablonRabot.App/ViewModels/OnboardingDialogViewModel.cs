using ShablonRabot.App.Infrastructure;

namespace ShablonRabot.App.ViewModels;

public sealed record OnboardingStep(string Glyph, string Title, string Text, string? Hint = null);

/// <summary>Обучение при первом запуске (DESIGN.md 20): несколько шагов, «Пропустить» на любом из них.</summary>
public sealed class OnboardingDialogViewModel : DialogViewModel
{
    int _index;

    public static readonly OnboardingStep[] Steps =
    [
        new("", "Добро пожаловать в «Чистовик»",
            "Программа делает шаблоны студенческих работ в Word — уже оформленные по ГОСТ или по методичке вашей кафедры, — и проверяет готовые работы перед сдачей.",
            "Для вузов, колледжей и техникумов: курсовая, ВКР и дипломная, реферат, отчёт по практике, индивидуальный проект и другие."),
        new("", "Выберите вид работы — или покажите пример",
            "Есть методичка или работа прошлых лет? Загрузите файл или вставьте требования текстом, даже из сообщения преподавателя: поля, шрифт и интервалы определятся сами, а программа подскажет, на какой ГОСТ это похоже.",
            "Вид работы → «Загрузить пример…» или «Вставить требования…»"),
        new("", "Заполните титул — и получите шаблон",
            "Титульный лист, правила оформления и структура — на следующих шагах. Справа видно, как будет выглядеть лист. Нажмите «Создать шаблон…» и пишите текст прямо в готовом документе Word: содержание, нумерация и подписи уже настроены.",
            "Ctrl+Enter — создать шаблон из любого раздела"),
        new("", "Проверьте работу перед сдачей",
            "В «Проверке работы» загрузите готовый .docx: программа найдёт ошибки оформления и может сохранить копию с примечаниями Word у каждого замечания. Ответы на вопросы о ГОСТах и Word — в «Базе знаний».",
            "Ctrl+5 — проверка работы · F1 — база знаний"),
    ];

    public OnboardingDialogViewModel(Action onFinished, Action openKnowledge)
    {
        Title = "Обучение";
        NextCommand = new RelayCommand(() => { if (IsLast) Finish(onFinished); else Index++; });
        BackCommand = new RelayCommand(() => Index--, () => _index > 0);
        SkipCommand = new RelayCommand(() => Finish(onFinished));
        KnowledgeCommand = new RelayCommand(() => { Finish(onFinished); openKnowledge(); });
        GoToCommand = new RelayCommand(p => { if (p is int i) Index = i; });
    }

    public RelayCommand NextCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand KnowledgeCommand { get; }
    public RelayCommand GoToCommand { get; }

    public int Index
    {
        get => _index;
        set
        {
            value = Math.Clamp(value, 0, Steps.Length - 1);
            if (!Set(ref _index, value)) return;
            OnPropertyChanged(string.Empty);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            StepChanged?.Invoke();
        }
    }

    public OnboardingStep Step => Steps[_index];
    public bool IsLast => _index == Steps.Length - 1;
    public bool IsFirst => _index == 0;
    public string NextText => IsLast ? "Начать" : "Далее";
    public string Counter => $"Шаг {_index + 1} из {Steps.Length}";
    public IEnumerable<bool> Dots => Steps.Select((_, i) => i == _index);
    public string AutomationText => $"{Counter}. {Step.Title}. {Step.Text}";
    /// <summary>Смена шага — для объявления экранным диктором.</summary>
    public Action? StepChanged { get; set; }

    bool _finished;
    void Finish(Action onFinished)
    {
        if (_finished) return;
        _finished = true;
        onFinished();
        Dismiss();
    }

    /// <summary>Esc — то же, что «Пропустить»: обучение больше не показывается.</summary>
    public override void Dismiss()
    {
        if (!_finished) { SkipCommand.Execute(null); return; }
        base.Dismiss();
    }
}
