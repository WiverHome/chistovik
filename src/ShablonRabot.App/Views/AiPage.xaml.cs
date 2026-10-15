using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class AiPage : UserControl
{
    AiViewModel? _ai;

    public AiPage()
    {
        InitializeComponent();
        // PasswordBox не привязывается к свойству: ключ передаём в модель вручную и не храним в разметке
        KeyBox.PasswordChanged += (_, _) =>
        {
            if (_ai != null) _ai.ApiKey = KeyBox.Password;
            UpdatePlaceholder();
        };
        UpdatePlaceholder();
        DataContextChanged += (_, e) =>
        {
            if (_ai != null) _ai.PropertyChanged -= OnAiChanged;
            _ai = (e.NewValue as MainViewModel)?.Ai;
            if (_ai == null) return;
            _ai.PropertyChanged += OnAiChanged;
            _ai.ResultShown = () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { ResultPanel.BringIntoView(); ResultBox.Focus(); });
            _ai.Refresh();
        };
        Unloaded += (_, _) => { if (_ai != null) _ai.PropertyChanged -= OnAiChanged; };
    }

    void OnAiChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // После подключения модель очищает ключ — очищаем и поле
        if (e.PropertyName == nameof(AiViewModel.ApiKey) && _ai?.ApiKey == "" && KeyBox.Password.Length > 0) KeyBox.Clear();
        // Начался ответ — показать его
        if (e.PropertyName == nameof(AiViewModel.IsRunning) && _ai?.IsRunning == true)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ResultPanel.BringIntoView());
        // Текст приходит частями — держим конец ответа на виду
        if (e.PropertyName == nameof(AiViewModel.Result) && _ai?.IsRunning == true) ResultBox.ScrollToEnd();
    }

    void UpdatePlaceholder() => KeyBox.Tag = KeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
}
