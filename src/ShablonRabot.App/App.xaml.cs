using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using ShablonRabot.App.Services;

namespace ShablonRabot.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = ru;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ru-RU")));
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, a) => { LogError(a.Exception); a.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception x) LogError(x); };
        // Клавиши доступа (подчёркнутые буквы) срабатывают только вместе с Alt: иначе набор текста
        // при фокусе на кнопке запускал бы команды (DESIGN.md 9)
        EventManager.RegisterClassHandler(typeof(UIElement), AccessKeyManager.AccessKeyPressedEvent,
            new AccessKeyPressedEventHandler((_, args) =>
            {
                if (!args.Handled && (Keyboard.Modifiers & ModifierKeys.Alt) == 0 && args.Scope == null)
                {
                    args.Target = null;
                    args.Handled = true;
                }
            }));

        var settings = AppSettings.Load();
        ThemeService.Apply(settings.Theme);
        ThemeService.Watch();
        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();

        // Профиль, переданный при запуске (двойной клик по .json, «Открыть с помощью»)
        var file = e.Args.FirstOrDefault(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (file != null) _ = window.ViewModel.ImportAsync(file);
        else if (e.Args.FirstOrDefault(Core.Detection.SampleAnalyzer.IsSupported) is { } sample)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => window.ViewModel.OpenDetectFile(sample));
        // Обучение — при первом запуске (и один раз после обновления, в котором оно появилось)
        else if (!settings.OnboardingDone)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => window.ViewModel.ShowOnboarding());
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogError(e.Exception);
        var saved = false;
        try { (MainWindow as MainWindow)?.ViewModel.SaveSession(); saved = true; } catch (Exception ex) { LogError(ex); }
        var text = $"Произошла непредвиденная ошибка:\n{e.Exception.Message}" + (saved ? "\n\nВведённые данные сохранены." : "")
                   + $"\n\nПодробности — в файле {ErrorLog}";
        try
        {
            // Окно, которое закрывается или ещё не показано, не может быть владельцем сообщения
            if (MainWindow is { IsVisible: true, IsLoaded: true } owner)
                MessageBox.Show(owner, text, "Чистовик", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show(text, "Чистовик", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex) { LogError(ex); }
    }

    static readonly string ErrorLog = System.IO.Path.Combine(AppSettings.Dir, "errors.log");

    /// <summary>Журнал непредвиденных ошибок: последние записи — в конце файла, файл не растёт больше ~1 МБ.</summary>
    static void LogError(Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Dir);
            if (System.IO.File.Exists(ErrorLog) && new System.IO.FileInfo(ErrorLog).Length > 1_000_000) System.IO.File.Delete(ErrorLog);
            System.IO.File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* журнал — не повод для новой ошибки */ }
    }
}
