using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class OnboardingDialogView : UserControl
{
    public OnboardingDialogView()
    {
        InitializeComponent();
        AutomationProperties.SetLiveSetting(Body, AutomationLiveSetting.Polite);
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not OnboardingDialogViewModel vm) return;
            // Новый шаг озвучивается; фокус остаётся на «Далее», чтобы листать клавишей Enter
            vm.StepChanged = () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                (UIElementAutomationPeer.FromElement(Body) ?? UIElementAutomationPeer.CreatePeerForElement(Body))
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                if (!Next.IsKeyboardFocusWithin) Next.Focus();
            });
        };
    }
}
