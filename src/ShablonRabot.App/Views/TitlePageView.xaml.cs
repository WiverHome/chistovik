using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShablonRabot.App.Controls;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class TitlePageView : UserControl
{
    public TitlePageView()
    {
        InitializeComponent();
        PageFocus.Attach(this, AppPage.TitlePage, key =>
        {
            // Поля «Шапки» спрятаны в свёрнутом блоке — раскрываем его
            if (key is "ministry" or "org_form")
                foreach (var ex in PageFocus.Descendants(this).OfType<Expander>()) ex.IsExpanded = true;
            UpdateLayout();
            PageFocus.FocusKey(this, key);
        });
        // Фокус в поле → обводка строки на листе
        AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => SetFocusedKey(e.NewFocus as DependencyObject)), true);
        AddHandler(LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.NewFocus is not System.Windows.Media.Visual v || !IsAncestorOf(v)) SetFocusedKey(null);
        }), true);
        Unloaded += (_, _) => SetFocusedKey(null);
    }

    void SetFocusedKey(DependencyObject? element)
    {
        if (DataContext is not MainViewModel vm) return;
        string? key = null;
        for (var d = element; d != null && d != this; d = PageFocus.Parent(d))
        {
            key = Input.GetFieldKey(d);
            if (key != null) break;
        }
        vm.Preview.FocusedKey = key;
    }
}
