using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Controls;

/// <summary>Отображает InfoBarViewModel (DataContext). Пустой DataContext — инфобар скрыт.</summary>
public partial class InfoBar : UserControl
{
    public InfoBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Render();
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

    InfoBarViewModel? Model => DataContext as InfoBarViewModel;

    void Render()
    {
        var m = Model;
        Visibility = m == null ? Visibility.Collapsed : Visibility.Visible;
        if (m == null) return;
        var (bg, glyph, fg) = m.Kind switch
        {
            InfoKind.Success => ("Brush.Bg.Success", "", "Brush.Text.Success"),
            InfoKind.Warning => ("Brush.Bg.Warning", "", "Brush.Text.Warning"),
            InfoKind.Error => ("Brush.Bg.Error", "", "Brush.Text.Error"),
            _ => ("Brush.Bg.Info", "", "Brush.Accent.Default"),
        };
        Bd.SetResourceReference(Border.BackgroundProperty, bg);
        Glyph.Text = glyph;
        Glyph.SetResourceReference(TextBlock.ForegroundProperty, fg);
        TitleRun.Text = m.Title ?? "";
        Gap.Text = string.IsNullOrEmpty(m.Title) || string.IsNullOrEmpty(m.Message) ? "" : "  ";
        MessageRun.Text = m.Message ?? "";
        ActionButton.Visibility = m.ActionText == null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = m.ActionText;
        CloseButton.Visibility = m.Closable ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, $"{m.Title} {m.Message}".Trim());
        if (AutomationPeer() is { } peer) peer.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    System.Windows.Automation.Peers.AutomationPeer? AutomationPeer() =>
        System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(this)
        ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(this);

    void OnAction(object sender, RoutedEventArgs e) => Model?.Action?.Invoke();
    void OnClose(object sender, RoutedEventArgs e) => Model?.Close?.Invoke();
}
