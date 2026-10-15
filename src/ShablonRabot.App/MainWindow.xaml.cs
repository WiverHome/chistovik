using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using ShablonRabot.App.Controls;
using ShablonRabot.App.Services;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    readonly AppSettings _settings;
    bool _navExpanded, _closingConfirmed, _compactLayout;
    IInputElement? _focusBeforeDialog;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        ViewModel = new MainViewModel(settings, new DialogService(() => this));
        DataContext = ViewModel;
        RestorePlacement();

        SourceInitialized += (_, _) => ThemeService.ApplyCaption(this);
        SizeChanged += (_, _) => UpdateLayoutMode();
        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += OnDragEnter;
        DragOver += OnDragEnter;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Loaded += (_, _) => FocusNavigation();
        LayoutRoot.PreviewMouseDown += (_, e) =>
        {
            if (_navExpanded && e.OriginalSource is DependencyObject d && !IsWithin(NavHost, d)) CollapseNav();
        };
    }

    public MainViewModel ViewModel { get; }

    /// <summary>Навигация показывает только иконки (окно уже 1180 и панель не раскрыта).</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    // ------------------------------------------------------------ раскладка (3.3)

    void UpdateLayoutMode()
    {
        var width = ActualWidth;
        _compactLayout = width < 1180;
        NavColumn.Width = new GridLength(_compactLayout ? 48 : 240);
        // Проверка работы и база знаний — без предпросмотра шаблона, на всю ширину
        PreviewColumn.Width = !ViewModel.ShowPreview ? new GridLength(0) : new GridLength(width >= 1440 ? 480 : _compactLayout ? 360 : 400);
        if (!_compactLayout) _navExpanded = false;
        ApplyNavState();
    }

    void ApplyNavState()
    {
        var overlay = _compactLayout && _navExpanded;
        NavHost.Width = _compactLayout && !_navExpanded ? 48 : 240;
        Burger.Visibility = _compactLayout ? Visibility.Visible : Visibility.Collapsed;
        SettingsText.Visibility = _compactLayout && !_navExpanded ? Visibility.Collapsed : Visibility.Visible;
        IsCompact = _compactLayout && !_navExpanded;
        if (overlay)
        {
            NavHost.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Flyout");
            NavHost.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 8, Direction = 270, Opacity = 0.14 };
        }
        else
        {
            NavHost.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Window");
            NavHost.Effect = null;
        }
        Burger.ToolTip = _navExpanded ? "Свернуть навигацию" : "Развернуть навигацию";
        System.Windows.Automation.AutomationProperties.SetName(Burger, (string)Burger.ToolTip);
    }

    static bool IsWithin(DependencyObject root, DependencyObject d)
    {
        for (var p = d; p != null; p = Views.PageFocus.Parent(p))
            if (p == root) return true;
        return false;
    }

    void OnBurger(object sender, RoutedEventArgs e)
    {
        _navExpanded = !_navExpanded;
        ApplyNavState();
    }

    void CollapseNav()
    {
        if (!_navExpanded) return;
        _navExpanded = false;
        ApplyNavState();
    }

    void OnNavSelected(object sender, SelectionChangedEventArgs e) => CollapseNav();

    void OnSettings(object sender, RoutedEventArgs e)
    {
        if (ViewModel.NavigateCommand.CanExecute("Settings")) ViewModel.NavigateCommand.Execute("Settings");
        CollapseNav();
    }

    void FocusNavigation()
    {
        if (NavList.ItemContainerGenerator.ContainerFromItem(NavList.SelectedItem) is ListBoxItem item) item.Focus();
        else if (ToolsNavList.ItemContainerGenerator.ContainerFromItem(ToolsNavList.SelectedItem) is ListBoxItem tool) tool.Focus();
    }

    // ------------------------------------------------------------ клавиатура

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ViewModel.Dialog is { CanDismiss: true } d) { d.Dismiss(); e.Handled = true; return; }
            if (_navExpanded) { CollapseNav(); e.Handled = true; }
            return;
        }
        // Ctrl+Z — отменить удаление/сброс, пока виден инфобар «Отменить» (в полях ввода — обычная отмена ввода)
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox
            && ViewModel.UndoCommand.CanExecute(null))
        {
            ViewModel.UndoCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // Ctrl+1…4 и Ctrl+, — раздел и фокус на его первый элемент
        if (Keyboard.Modifiers == ModifierKeys.Control && ViewModel.Dialog == null)
        {
            string? target = e.Key switch
            {
                Key.D1 or Key.NumPad1 => "Type", Key.D2 or Key.NumPad2 => "TitlePage",
                Key.D3 or Key.NumPad3 => "Rules", Key.D4 or Key.NumPad4 => "Structure",
                Key.D5 or Key.NumPad5 => "Check", Key.D6 or Key.NumPad6 => "Knowledge", Key.OemComma => "Settings", _ => null,
            };
            if (target != null)
            {
                ViewModel.NavigateCommand.Execute(target);
                CollapseNav();
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Editor.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
                e.Handled = true;
                return;
            }
        }
        // F1 — база знаний
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None && ViewModel.Dialog == null)
        {
            ViewModel.NavigateCommand.Execute("Knowledge");
            CollapseNav();
            e.Handled = true;
            return;
        }
        // F6 / Shift+F6 — переход между областями окна
        if (e.Key == Key.F6 && ViewModel.Dialog == null)
        {
            MoveBetweenRegions(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
    }

    void MoveBetweenRegions(int direction)
    {
        FrameworkElement[] regions = ViewModel.ShowPreview ? [NavHost, Editor, PreviewHost] : [NavHost, Editor];
        var focused = Keyboard.FocusedElement as DependencyObject;
        var current = Array.FindIndex(regions, r => focused != null && (r == focused || (focused is System.Windows.Media.Visual v && r.IsAncestorOf(v))));
        var next = regions[((current < 0 ? 0 : current + direction) + regions.Length) % regions.Length];
        if (next == NavHost) { FocusNavigation(); return; }
        next.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    // ------------------------------------------------------------ диалоги

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) { UpdateLayoutMode(); AnimatePage(); return; }
        if (e.PropertyName != nameof(MainViewModel.Dialog)) return;
        if (ViewModel.Dialog != null) AnimateDialog();
        if (ViewModel.Dialog is GenerationDialogViewModel g)
            g.PropertyChanged += (_, pe) =>
            {
                if (pe.PropertyName is nameof(GenerationDialogViewModel.Phase) or "")
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusDialog);
            };
        if (ViewModel.Dialog != null)
        {
            _focusBeforeDialog ??= Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusDialog);
        }
        else
        {
            var restore = _focusBeforeDialog;
            _focusBeforeDialog = null;
            if (restore is UIElement { IsVisible: true } el) el.Focus();
            else Editor.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    // ------------------------------------------------------------ движение (11.7)

    static bool Animations => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    /// <summary>Смена раздела: прозрачность 0→1 и сдвиг 12 px снизу вверх, 250 мс.</summary>
    void AnimatePage()
    {
        if (!Animations) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform(0, 12);
        Editor.RenderTransform = shift;
        Editor.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
    }

    /// <summary>Появление диалога: масштаб 1,05→1 и прозрачность, 167 мс.</summary>
    void AnimateDialog()
    {
        if (!Animations) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new ScaleTransform(1.05, 1.05);
        DialogContent.RenderTransformOrigin = new Point(0.5, 0.5);
        DialogContent.RenderTransform = scale;
        var d = TimeSpan.FromMilliseconds(167);
        DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.05, 1, d) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.05, 1, d) { EasingFunction = ease });
    }

    void FocusDialog()
    {
        var target = FindDescendants(DialogContent).FirstOrDefault(d => Input.GetDefaultFocus(d) && d is UIElement { IsVisible: true });
        if (target is UIElement el) el.Focus();
        else DialogContent.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    static IEnumerable<DependencyObject> FindDescendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in FindDescendants(child)) yield return d;
        }
    }

    void OnSmokeClick(object sender, MouseButtonEventArgs e)
    {
        // Клик мимо диалога не закрывает его (чтобы случайно не потерять ввод), только возвращает фокус
        FocusDialog();
    }

    // ------------------------------------------------------------ перетаскивание .json

    /// <summary>Первый подходящий файл: .json — профиль, .docx/.doc/.rtf/.odt/.pdf — пример работы (DESIGN.md 17.3).</summary>
    static (string Path, bool IsProfile)? Dropped(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        foreach (var f in files)
        {
            if (string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase)) return (f, true);
            if (Core.Detection.SampleAnalyzer.IsSupported(f)) return (f, false);
        }
        return null;
    }

    bool CanAccept((string Path, bool IsProfile)? drop) =>
        drop is { } d && (ViewModel.Dialog == null || (!d.IsProfile && ViewModel.Dialog is DetectDialogViewModel { IsAnalyzing: false }));

    void OnDragEnter(object sender, DragEventArgs e)
    {
        var drop = Dropped(e);
        var ok = CanAccept(drop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        if (ok)
        {
            DropIcon.Text = drop!.Value.IsProfile ? "\uE8B5" : "\uE721";
            DropText.Text = drop.Value.IsProfile ? "Отпустите, чтобы импортировать профиль"
                : IsCheckDrop(drop.Value.Path) ? "Отпустите, чтобы проверить работу" : "Отпустите, чтобы определить правила по примеру";
        }
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var drop = Dropped(e);
        if (!CanAccept(drop)) return;
        if (drop!.Value.IsProfile) _ = ViewModel.ImportAsync(drop.Value.Path);
        else if (IsCheckDrop(drop.Value.Path)) _ = ViewModel.Check.CheckAsync(drop.Value.Path);
        else ViewModel.OpenDetectFile(drop.Value.Path);
    }

    /// <summary>На странице «Проверка работы» документ перетаскивают для проверки, а не для определения правил.</summary>
    bool IsCheckDrop(string path) => ViewModel.CurrentPage == AppPage.Check && ViewModel.Dialog == null && !ViewModel.Check.IsChecking;

    // ------------------------------------------------------------ размер окна и закрытие

    void RestorePlacement()
    {
        if (_settings.Window is not { Width: > 0 } w) return;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.IntersectsWith(new Rect(w.Left, w.Top, w.Width, w.Height))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (w.Left, w.Top, Math.Max(w.Width, MinWidth), Math.Max(w.Height, MinHeight));
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closingConfirmed) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (!await ViewModel.ConfirmCloseAsync()) return;
        var b = RestoreBounds;
        _settings.Window = new WindowPlacement
        {
            Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height, Maximized = WindowState == WindowState.Maximized,
        };
        _settings.Save();
        _closingConfirmed = true;
        // Подтверждение часто приходит синхронно — мы ещё внутри Closing, а повторный Close() там запрещён (WPF).
        // Закрываем окно после выхода из обработчика.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
