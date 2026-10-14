using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class StructurePage : UserControl
{
    string? _titleBeforeEdit;

    public StructurePage()
    {
        InitializeComponent();
        PageFocus.Attach(this, ViewModels.AppPage.Structure);
        Loaded += (_, _) => { if (Structure is { } s) s.FocusItem += OnFocusItem; };
        Unloaded += (_, _) =>
        {
            if (Structure is { } s) s.FocusItem -= OnFocusItem;
            if (DataContext is MainViewModel vm) vm.Preview.FocusedOutline = null;
        };
        // 41: подсветка строки в «Содержании» пропадает вместе с фокусом
        AddHandler(LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) =>
        {
            if (e.NewFocus is not TextBox { DataContext: OutlineItem } && DataContext is MainViewModel vm) vm.Preview.FocusedOutline = null;
        }), true);
    }

    StructureViewModel? Structure => (DataContext as MainViewModel)?.Structure;

    /// <summary>Новая или перемещённая строка забирает фокус.</summary>
    void OnTitleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: OutlineItem { WantsFocus: true } item } box)
        {
            item.WantsFocus = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { box.Focus(); box.CaretIndex = box.Text.Length; box.BringIntoView(); });
        }
    }

    void OnFocusItem(OutlineItem item) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        var box = PageFocus.Descendants(this).OfType<TextBox>().FirstOrDefault(t => t.DataContext == item);
        box?.Focus();
    });

    void OnTitleFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: OutlineItem item } || DataContext is not MainViewModel vm) return;
        _titleBeforeEdit = item.Title;
        vm.Preview.FocusedOutline = item;
    }

    void OnTitleKey(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: OutlineItem item } box || Structure is not { } s) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var alt = Keyboard.Modifiers == ModifierKeys.Alt;

        if (alt && key == Key.Up) Run(s.MoveUpCommand, item);
        else if (alt && key == Key.Down) Run(s.MoveDownCommand, item);
        else if (alt && key == Key.Right && item is ChapterViewModel) Run(s.IndentCommand, item);
        else if (alt && key == Key.Left && item is SectionViewModel) Run(s.OutdentCommand, item);
        else if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) Run(s.AddSectionAfterCommand, item);
        else if (key == Key.Delete && (box.Text.Length == 0 || Keyboard.Modifiers == ModifierKeys.Control)) Run(s.DeleteCommand, item);
        else if (key == Key.Escape && _titleBeforeEdit != null && box.Text != _titleBeforeEdit) item.Title = _titleBeforeEdit;
        else if (key == Key.Up && Keyboard.Modifiers == ModifierKeys.None) box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
        else if (key == Key.Down && Keyboard.Modifiers == ModifierKeys.None) box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
        else return;
        e.Handled = true;
    }

    // ------------------------------------------------------------ перетаскивание за ручку

    Point _dragStart;

    void OnGripDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    void OnGripMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: OutlineItem item } grip) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = FindRow(grip);
        if (row != null) row.Opacity = 0.5;
        DragDrop.DoDragDrop(grip, new DataObject(typeof(OutlineItem), item), DragDropEffects.Move);
        if (row != null) row.Opacity = 1;
    }

    static Border? FindRow(DependencyObject d)
    {
        for (var p = d; p != null; p = PageFocus.Parent(p))
            if (p is Border { AllowDrop: true } b) return b;
        return null;
    }

    void OnRowDragOver(object sender, DragEventArgs e)
    {
        var ok = sender is Border { DataContext: OutlineItem target }
                 && e.Data.GetData(typeof(OutlineItem)) is OutlineItem source && StructureViewModel.CanDrop(source, target);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (sender is Border b)
        {
            // Место вставки — акцентная линия у верхнего края строки
            b.BorderThickness = new Thickness(0, ok ? 2 : 0, 0, 0);
            b.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent.Default");
        }
        e.Handled = true;
    }

    void OnRowDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
    }

    void OnRowDrop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
        if (sender is Border { DataContext: OutlineItem target } && e.Data.GetData(typeof(OutlineItem)) is OutlineItem source)
            Structure?.DropOnto(source, target);
        e.Handled = true;
    }

    static void Run(ICommand command, object parameter)
    {
        if (command.CanExecute(parameter)) command.Execute(parameter);
    }
}
