using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class CheckPage : UserControl
{
    public CheckPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not MainViewModel main) return;
            main.Check.RefreshRules();
            // Итог проверки озвучивается и получает фокус, чтобы сразу перейти к замечаниям
            main.Check.Announce = () => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (main.Check.IsResult) { Scroll.ScrollToTop(); SummaryCard.Focus(); }
                (UIElementAutomationPeer.FromElement(SummaryCard) ?? UIElementAutomationPeer.CreatePeerForElement(SummaryCard))
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            });
        };
        AutomationProperties.SetLiveSetting(SummaryCard, AutomationLiveSetting.Polite);
    }
}
