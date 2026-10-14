using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class PreviewSheet : UserControl
{
    public PreviewSheet()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnPreviewChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnPreviewChanged;
            ApplyHighlight();
        };
    }

    // ------------------------------------------------------------ строки с подсказками

    public static readonly DependencyProperty RunsProperty = DependencyProperty.RegisterAttached(
        "Runs", typeof(IReadOnlyList<PreviewRun>), typeof(PreviewSheet), new PropertyMetadata(null, OnRunsChanged));

    public static IReadOnlyList<PreviewRun>? GetRuns(DependencyObject d) => (IReadOnlyList<PreviewRun>?)d.GetValue(RunsProperty);
    public static void SetRuns(DependencyObject d, IReadOnlyList<PreviewRun>? value) => d.SetValue(RunsProperty, value);

    static void OnRunsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        tb.MouseLeftButtonDown -= OnRunClick;
        if (e.NewValue is not IReadOnlyList<PreviewRun> runs) return;
        foreach (var r in runs)
        {
            var parts = r.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) tb.Inlines.Add(new LineBreak());
                if (parts[i].Length == 0) continue;
                var run = new Run(parts[i]) { Tag = r.FieldKey };
                if (r.IsPlaceholder)
                {
                    run.SetResourceReference(TextElement.BackgroundProperty, "Brush.Fill.Placeholder");
                    run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Paper.PlaceholderText");
                }
                if (r.FieldKey != null) run.Cursor = Cursors.Hand;
                tb.Inlines.Add(run);
            }
        }
        if (runs.Any(r => r.FieldKey != null)) tb.MouseLeftButtonDown += OnRunClick;
    }

    /// <summary>Клик по фрагменту листа — переход к полю титульного листа.</summary>
    static void OnRunClick(object sender, MouseButtonEventArgs e)
    {
        var key = (e.OriginalSource as Run)?.Tag as string
                  ?? GetRuns((DependencyObject)sender)?.FirstOrDefault(r => r.FieldKey != null)?.FieldKey;
        if (key == null || Window.GetWindow((DependencyObject)sender)?.DataContext is not MainViewModel vm) return;
        if (vm.Dialog is ZoomDialogViewModel z) z.Dismiss();
        vm.FocusField(key);
        e.Handled = true;
    }

    // ------------------------------------------------------------ подсветка правила на схеме (5.3)

    void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PreviewViewModel.Highlight) or "") ApplyHighlight();
    }

    void ApplyHighlight()
    {
        var h = (DataContext as PreviewViewModel)?.Highlight;
        var active = (Brush)FindResource("Brush.Fill.MarginZoneActive");
        var accent = (Brush)FindResource("Brush.Accent.Default");
        void Zone(Rectangle r, bool on) => r.Fill = on ? active : Brushes.Transparent;
        var all = h == "margins";
        Zone(ZoneLeft, all || h == "margin-left");
        Zone(ZoneRight, all || h == "margin-right");
        Zone(ZoneTop, all || h == "margin-top");
        Zone(ZoneBottom, all || h == "margin-bottom");
        IndentMark.Visibility = h == "indent" ? Visibility.Visible : Visibility.Collapsed;
        SpacingMark.Visibility = h == "lineSpacing" ? Visibility.Visible : Visibility.Collapsed;
        PageNumberRing.BorderBrush = h == "pageNumber" ? accent : Brushes.Transparent;
        FigureCaptionBox.BorderBrush = h is "figurePrefix" or "separator" ? accent : Brushes.Transparent;
        TableCaptionBox.BorderBrush = h is "tableCaptionAlign" or "separator" ? accent : Brushes.Transparent;
        TableBox.BorderThickness = new Thickness(h == "tableFontSize" ? 4 : 1);
        TableBox.BorderBrush = h == "tableFontSize" ? accent : Brushes.Black;
    }

    // ------------------------------------------------------------ конвертеры

    public static readonly IValueConverter LineHeight = new Func<object, object>(v => (double)v * 1.15).ToConverter();
    /// <summary>Нижние строки титула (город, год) — 10 мм от края, как колонтитул первой страницы.</summary>
    public static readonly IValueConverter BottomFooter = new Func<object, object>(v => v is Thickness t ? new Thickness(t.Left, 0, t.Right, 37.8) : v).ToConverter();
    public static readonly IValueConverter HorizontalOnly = new Func<object, object>(v => v is Thickness t ? new Thickness(t.Left, 0, t.Right, 0) : v).ToConverter();
    /// <summary>Подпись верхнего поля — по центру поля по вертикали.</summary>
    public static readonly IValueConverter TopLabel = new Func<object, object>(v => new Thickness(0, Math.Max(0, (double)v / 2 - 16), 0, 0)).ToConverter();
    /// <summary>Подпись верхнего поля — слева в поле (номер страницы может стоять вверху по центру).</summary>
    public static readonly IValueConverter TopLabelLeft = new Func<object, object>(v => v is Thickness t ? new Thickness(t.Left + 8, Math.Max(0, t.Top / 2 - 16), 0, 0) : v).ToConverter();
    /// <summary>Подпись нижнего поля — слева в поле, чтобы не закрывать номер страницы.</summary>
    public static readonly IValueConverter BottomLabel = new Func<object, object>(v => v is Thickness t ? new Thickness(t.Left + 8, 0, 0, Math.Max(0, t.Bottom / 2 - 16)) : v).ToConverter();
    public static readonly IValueConverter SideLabel = new Func<object, object>(v => new Thickness(Math.Max(0, (double)v / 2 - 16), 0, 0, 0)).ToConverter();
    public static readonly IValueConverter SideLabelRight = new Func<object, object>(v => new Thickness(0, 0, Math.Max(0, (double)v / 2 - 16), 0)).ToConverter();
    public static readonly IValueConverter CenterInMargin = new Func<object, object>(v => Math.Max(0, (double)v / 2 - 16)).ToConverter();
}

static class ConverterExtensions
{
    sealed class FuncConverter(Func<object, object> f) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value == null || value == DependencyProperty.UnsetValue ? Binding.DoNothing : f(value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public static IValueConverter ToConverter(this Func<object, object> f) => new FuncConverter(f);
}
