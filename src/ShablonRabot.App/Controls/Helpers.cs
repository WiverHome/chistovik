using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace ShablonRabot.App.Controls;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

/// <summary>null, пустая строка или 0 → Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null || value is string { Length: 0 } || value is 0;
        return empty ^ Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>value == parameter (по строковому представлению) → true. Для перечислений в RadioButton/видимости.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var eq = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        return targetType == typeof(Visibility) ? (eq ? Visibility.Visible : Visibility.Collapsed) : eq;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string s && targetType.IsEnum ? Enum.Parse(targetType, s) : Binding.DoNothing;
}

/// <summary>Вспомогательные присоединённые свойства для полей ввода.</summary>
public static class Input
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Input), new FrameworkPropertyMetadata(""));

    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string value) => d.SetValue(PlaceholderProperty, value);

    /// <summary>Только цифры (год, курс): прочие символы не вводятся.</summary>
    public static readonly DependencyProperty DigitsOnlyProperty = DependencyProperty.RegisterAttached(
        "DigitsOnly", typeof(bool), typeof(Input), new PropertyMetadata(false, OnDigitsOnly));

    public static bool GetDigitsOnly(DependencyObject d) => (bool)d.GetValue(DigitsOnlyProperty);
    public static void SetDigitsOnly(DependencyObject d, bool value) => d.SetValue(DigitsOnlyProperty, value);

    static void OnDigitsOnly(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.PreviewTextInput -= Digits;
        if (e.NewValue is true) tb.PreviewTextInput += Digits;
    }

    static void Digits(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

    /// <summary>Запрет Enter в многострочном поле темы (перенос строк — только автоматический).</summary>
    public static readonly DependencyProperty NoEnterProperty = DependencyProperty.RegisterAttached(
        "NoEnter", typeof(bool), typeof(Input), new PropertyMetadata(false, (d, e) =>
        {
            if (d is TextBox tb && e.NewValue is true)
                tb.PreviewKeyDown += (_, k) => { if (k.Key == Key.Enter) k.Handled = true; };
        }));

    public static bool GetNoEnter(DependencyObject d) => (bool)d.GetValue(NoEnterProperty);
    public static void SetNoEnter(DependencyObject d, bool value) => d.SetValue(NoEnterProperty, value);

    /// <summary>Поле без рамки и нижней линии в покое (названия в списке структуры).</summary>
    public static readonly DependencyProperty InlineProperty = DependencyProperty.RegisterAttached(
        "Inline", typeof(bool), typeof(Input), new PropertyMetadata(false));

    public static bool GetInline(DependencyObject d) => (bool)d.GetValue(InlineProperty);
    public static void SetInline(DependencyObject d, bool value) => d.SetValue(InlineProperty, value);

    /// <summary>Элемент получает фокус при открытии диалога.</summary>
    public static readonly DependencyProperty DefaultFocusProperty = DependencyProperty.RegisterAttached(
        "DefaultFocus", typeof(bool), typeof(Input), new PropertyMetadata(false));

    public static bool GetDefaultFocus(DependencyObject d) => (bool)d.GetValue(DefaultFocusProperty);
    public static void SetDefaultFocus(DependencyObject d, bool value) => d.SetValue(DefaultFocusProperty, value);

    /// <summary>Ключ поля титульного листа: для связи «поле ↔ строка на листе» и перехода к полю по клику.</summary>
    public static readonly DependencyProperty FieldKeyProperty = DependencyProperty.RegisterAttached(
        "FieldKey", typeof(string), typeof(Input), new PropertyMetadata(null));

    public static string? GetFieldKey(DependencyObject d) => (string?)d.GetValue(FieldKeyProperty);
    public static void SetFieldKey(DependencyObject d, string? value) => d.SetValue(FieldKeyProperty, value);
}

/// <summary>Строка правила: название, описание, отметка «Изменено» и контрол справа (или снизу при узкой ширине).</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = Dp<string>(nameof(Header));
    public static readonly DependencyProperty DescriptionProperty = Dp<string>(nameof(Description));
    public static readonly DependencyProperty IsChangedProperty = Dp<bool>(nameof(IsChanged));
    public static readonly DependencyProperty ChangedTextProperty = Dp<string>(nameof(ChangedText));
    public static readonly DependencyProperty RevertCommandProperty = Dp<ICommand>(nameof(RevertCommand));
    public static readonly DependencyProperty RevertParameterProperty = Dp<object>(nameof(RevertParameter));
    public static readonly DependencyProperty RevertNameProperty = Dp<string>(nameof(RevertName));
    public static readonly DependencyProperty StackedProperty = Dp<bool>(nameof(Stacked));
    /// <summary>Ошибки и предупреждения правила — под строкой, на всю ширину.</summary>
    public static readonly DependencyProperty FooterProperty = Dp<object>(nameof(Footer));

    static DependencyProperty Dp<T>(string name) => DependencyProperty.Register(name, typeof(T), typeof(SettingRow));

    public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public bool IsChanged { get => (bool)GetValue(IsChangedProperty); set => SetValue(IsChangedProperty, value); }
    public string? ChangedText { get => (string?)GetValue(ChangedTextProperty); set => SetValue(ChangedTextProperty, value); }
    public ICommand? RevertCommand { get => (ICommand?)GetValue(RevertCommandProperty); set => SetValue(RevertCommandProperty, value); }
    public object? RevertParameter { get => GetValue(RevertParameterProperty); set => SetValue(RevertParameterProperty, value); }
    public string? RevertName { get => (string?)GetValue(RevertNameProperty); set => SetValue(RevertNameProperty, value); }
    public bool Stacked { get => (bool)GetValue(StackedProperty); set => SetValue(StackedProperty, value); }
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    public SettingRow()
    {
        SizeChanged += (_, e) => { if (!_forceStacked) Stacked = e.NewSize.Width < 500; };
    }

    bool _forceStacked;

    /// <summary>Строка «стопкой» всегда (поля страницы).</summary>
    public bool AlwaysStacked
    {
        get => _forceStacked;
        set { _forceStacked = value; Stacked = value; }
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        System.Windows.Automation.AutomationProperties.SetName(this, Header ?? "");
        if (Description != null) System.Windows.Automation.AutomationProperties.SetHelpText(this, Description);
    }
}

/// <summary>
/// Форма в две колонки: элемент с FormPanel.Half=True занимает половину строки, остальные — всю.
/// При ширине меньше SingleColumnWidth всё в одну колонку.
/// </summary>
public sealed class FormPanel : Panel
{
    public static readonly DependencyProperty HalfProperty = DependencyProperty.RegisterAttached(
        "Half", typeof(bool), typeof(FormPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetHalf(DependencyObject d) => (bool)d.GetValue(HalfProperty);
    public static void SetHalf(DependencyObject d, bool value) => d.SetValue(HalfProperty, value);

    public double ColumnGap { get; set; } = 16;
    public double RowGap { get; set; } = 12;
    public double SingleColumnWidth { get; set; } = 480;

    IEnumerable<List<UIElement>> Rows(double width)
    {
        var single = width < SingleColumnWidth;
        var row = new List<UIElement>();
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var half = !single && GetHalf(child);
            if (!half)
            {
                if (row.Count > 0) { yield return row; row = new(); }
                yield return [child];
                continue;
            }
            row.Add(child);
            if (row.Count == 2) { yield return row; row = new(); }
        }
        if (row.Count > 0) yield return row;
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 600 : available.Width;
        double height = 0;
        var first = true;
        foreach (var row in Rows(width))
        {
            var cell = row.Count == 2 || (row.Count == 1 && GetHalf(row[0]) && width >= SingleColumnWidth) ? (width - ColumnGap) / 2 : width;
            double h = 0;
            foreach (var c in row)
            {
                c.Measure(new Size(cell, double.PositiveInfinity));
                h = Math.Max(h, c.DesiredSize.Height);
            }
            height += h + (first ? 0 : RowGap);
            first = false;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var width = final.Width;
        double y = 0;
        var first = true;
        foreach (var row in Rows(width))
        {
            if (!first) y += RowGap;
            first = false;
            var halfMode = row.Count == 2 || (row.Count == 1 && GetHalf(row[0]) && width >= SingleColumnWidth);
            var cell = halfMode ? (width - ColumnGap) / 2 : width;
            var h = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++)
                row[i].Arrange(new Rect(i * (cell + ColumnGap), y, cell, h));
            y += h;
        }
        return final;
    }
}

/// <summary>Конвертеры для x:Static.</summary>
public static class Converters
{
    public static readonly IMultiValueConverter AllTrue = new AllTrueConverter();
    public static readonly IValueConverter Not = new NotConverter();
    /// <summary>«Со_хранить…» → «Сохранить» для имени в UI Automation.</summary>
    public static readonly IValueConverter StripAccessKey = new StripConverter();

    sealed class StripConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            (value as string ?? "").Replace("_", "").TrimEnd('…');
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    sealed class AllTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var all = values.All(v => v is true);
            return targetType == typeof(Visibility) ? (all ? Visibility.Visible : Visibility.Collapsed) : all;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
    }

    sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    }
}
