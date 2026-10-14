using System.ComponentModel;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class GenerationDialogView : UserControl
{
    public GenerationDialogView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not GenerationDialogViewModel vm) return;
            vm.PropertyChanged += (_, pe) =>
            {
                if (pe.PropertyName is nameof(GenerationDialogViewModel.Title) or nameof(GenerationDialogViewModel.Hint) or "") Announce();
            };
            foreach (var step in vm.Steps)
                step.PropertyChanged += (_, pe) => { if (pe.PropertyName == nameof(GenerationStep.State)) Announce(); };
        };
    }

    /// <summary>Диктор сообщает о смене этапа и итоге (DESIGN.md 6.2).</summary>
    void Announce() => Dispatcher.BeginInvoke(() =>
    {
        var peer = UIElementAutomationPeer.FromElement(TitleText) ?? UIElementAutomationPeer.CreatePeerForElement(TitleText);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    });
}
