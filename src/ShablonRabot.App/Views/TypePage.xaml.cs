using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ShablonRabot.App.Views;

public partial class TypePage : UserControl
{
    public TypePage() => InitializeComponent();

    /// <summary>Фокус пришёл стрелкой — карточка выбирается (DESIGN.md 9).</summary>
    void OnCardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is not RadioButton { DataContext: ViewModels.ProfileCard { IsSelectable: true } card } rb || e.NewFocus != rb) return;
        var arrow = new[] { System.Windows.Input.Key.Left, System.Windows.Input.Key.Right, System.Windows.Input.Key.Up, System.Windows.Input.Key.Down }
            .Any(System.Windows.Input.Keyboard.IsKeyDown);
        if (arrow) card.IsChecked = true;
    }

    /// <summary>Кнопка «…» на карточке открывает меню действий.</summary>
    void OnMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.Tag = DataContext;
        menu.DataContext = button.DataContext;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
