using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ShablonRabot.App.Infrastructure;
using ShablonRabot.App.ViewModels;
using ShablonRabot.Core;

namespace ShablonRabot.App.Views;

public partial class DetectDialogView : UserControl
{
    public DetectDialogView()
    {
        ApplyOrAnalyzeCommand = new RelayCommand(() =>
        {
            if (DataContext is not DetectDialogViewModel vm) return;
            var cmd = vm.IsResult ? vm.ApplyCommand : vm.AnalyzeTextCommand;
            if (cmd.CanExecute(null)) cmd.Execute(null);
        });
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not DetectDialogViewModel vm) return;
            vm.FocusText = () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { RequirementsInput.Focus(); RequirementsInput.CaretIndex = RequirementsInput.Text.Length; });
            vm.Announce = () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                AutomationProperties.SetName(Body, vm.IsError ? $"{vm.ErrorTitle}. {vm.ErrorText}" : vm.AnnounceText);
                AutomationProperties.SetLiveSetting(Body, AutomationLiveSetting.Assertive);
                (UIElementAutomationPeer.FromElement(Body) ?? UIElementAutomationPeer.CreatePeerForElement(Body))
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                // После анализа фокус — на основную кнопку или первую строку
                if (vm.IsResult) Body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            });
            if (vm.Source == DetectSource.Text) vm.FocusText();
        };
    }

    public RelayCommand ApplyOrAnalyzeCommand { get; }

    public string FormatsText => TemplateService.IsWordInstalled
        ? ".docx — точнее всего. .doc, .rtf и .odt откроет Microsoft Word, PDF — приблизительно."
        : "Microsoft Word не найден — сейчас можно открыть .docx и PDF.";

    public static readonly IValueConverter MaxHeightConverter = new Func<object, object>(v => Math.Max(320, (double)v - 48 - 32)).ToConverter();

    /// <summary>Общий чекбокс: из промежуточного состояния — «отметить всё».</summary>
    void OnAllClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is DetectDialogViewModel vm) vm.AllChecked = vm.CheckedCount < vm.ApplicableCount;
    }

    /// <summary>Клик в любом месте строки переключает чекбокс (17.4.5).</summary>
    void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && PageFocus.Parent(d) is CheckBox) return;
        if (sender is FrameworkElement { DataContext: DetectedRowViewModel { IsSame: false } row }) row.IsChecked = !row.IsChecked;
    }
}
