using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShablonRabot.App.Controls;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

/// <summary>Общая логика страниц: переход к полю по запросу VM и к первому полю с ошибкой.</summary>
static class PageFocus
{
    public static void Attach(UserControl page, AppPage kind, Action<string>? custom = null)
    {
        void Handler(AppPage target, string key)
        {
            if (target != kind) return;
            page.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (custom != null) { custom(key); return; }
                FocusKey(page, key);
            });
        }

        page.Loaded += (_, _) => { if (page.DataContext is MainViewModel vm) vm.FocusRequested += Handler; };
        page.Unloaded += (_, _) => { if (page.DataContext is MainViewModel vm) vm.FocusRequested -= Handler; };
    }

    public static void FocusKey(DependencyObject root, string key)
    {
        var target = key == "error"
            ? Descendants(root).OfType<FrameworkElement>().FirstOrDefault(e => Validation.GetHasError(e) && e.IsVisible)
            : Descendants(root).OfType<FrameworkElement>().FirstOrDefault(e => Input.GetFieldKey(e) == key && e.IsVisible);
        if (target == null) return;
        target.BringIntoView();
        if (target is NumberBox nb) nb.Box.Focus();
        else if (target.Focusable) Keyboard.Focus(target);
        else target.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        if (Keyboard.FocusedElement is TextBox tb) tb.SelectAll();
    }

    /// <summary>Родитель в дереве: для фрагментов текста (Run и т. п.) — через логическое дерево.</summary>
    public static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
