using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ShablonRabot.App.Controls;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class RulesPage : UserControl
{
    public RulesPage()
    {
        InitializeComponent();
        PageFocus.Attach(this, ViewModels.AppPage.Rules, key =>
        {
            if (!key.StartsWith("rule:")) { PageFocus.FocusKey(this, key); return; }
            // Фокус на контрол строки правила (после «Вернуть»)
            var row = PageFocus.Descendants(this).OfType<SettingRow>().FirstOrDefault(r => Equals(r.Tag, key[5..]));
            if (row?.Content is UIElement control && !control.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
                control.Focus();
        });
        // Фокус или наведение на правило подсвечивает его на схеме страницы
        AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => Highlight(e.NewFocus as DependencyObject)), true);
        AddHandler(MouseMoveEvent, new MouseEventHandler((_, e) => Highlight(e.OriginalSource as DependencyObject)), true);
        MouseLeave += (_, _) => Highlight(Keyboard.FocusedElement as DependencyObject);
        Unloaded += (_, _) => { if (DataContext is MainViewModel vm) vm.Preview.Highlight = null; };
    }

    void Highlight(DependencyObject? source)
    {
        if (DataContext is not MainViewModel vm) return;
        string? key = null;
        for (var d = source; d != null && d != this; d = PageFocus.Parent(d))
        {
            if (d is FrameworkElement { Tag: string k } && d is SettingRow) { key = k; break; }
            if (Input.GetFieldKey(d) is string fk && fk.StartsWith("margin-")) { key = fk; break; }
        }
        vm.Preview.Highlight = key;
    }

    void OnMarginsSize(object sender, SizeChangedEventArgs e)
    {
        if (sender is UniformGrid g) g.Columns = e.NewSize.Width < 4 * 112 + 36 ? 2 : 4;
    }

    void OnReset(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.IsImportedProfile) { vm.ResetRules("gost"); return; }
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var toProfile = new MenuItem { Header = vm.ResetToProfileText };
        toProfile.Click += (_, _) => vm.ResetRules("profile");
        var toGost = new MenuItem { Header = $"К {RulesViewModel.DefaultGost}" };
        toGost.Click += (_, _) => vm.ResetRules("gost");
        menu.Items.Add(toProfile);
        menu.Items.Add(toGost);
        menu.IsOpen = true;
    }
}
