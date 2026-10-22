using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace ShablonRabot.App.Controls;

public partial class NumberBox : UserControl
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(NumberBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumberBox)d).UpdateState()));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumberBox));
    public static readonly DependencyProperty MinProperty = DependencyProperty.Register(nameof(Min), typeof(double), typeof(NumberBox), new PropertyMetadata(0.0));
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max), typeof(double), typeof(NumberBox), new PropertyMetadata(100.0));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumberBox), new PropertyMetadata(1.0));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(NumberBox), new PropertyMetadata(0));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(NumberBox),
        new PropertyMetadata("", (d, e) => ((NumberBox)d).ApplyLabel((string)e.NewValue)));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Unit { get => (string?)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public double Min { get => (double)GetValue(MinProperty); set => SetValue(MinProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    /// <summary>Название поля для диктора и подсказок кнопок («левое поле»).</summary>
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public NumberBox()
    {
        InitializeComponent();
        Validation.AddErrorHandler(this, (_, _) => UpdateState());
        Box.GotKeyboardFocus += (_, _) => UpdateState();
        Box.LostKeyboardFocus += (_, _) => UpdateState();
        MouseEnter += (_, _) => UpdateState();
        MouseLeave += (_, _) => UpdateState();
        DependencyPropertyDescriptorHelper.OnHasErrorChanged(this, UpdateState);
        IsEnabledChanged += (_, _) => UpdateState();
        Loaded += (_, _) => UpdateState();
    }

    void ApplyLabel(string label)
    {
        AutomationProperties.SetName(Box, label);
        AutomationProperties.SetName(Up, $"Увеличить: {label}");
        AutomationProperties.SetName(Down, $"Уменьшить: {label}");
        Up.ToolTip = $"Увеличить: {label}";
        Down.ToolTip = $"Уменьшить: {label}";
    }

    public static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace('.', ','), NumberStyles.Float, Ru, out value);

    string Format(double v) => v.ToString(Decimals > 0 ? "0." + new string('#', Decimals) : "0", Ru);

    void Nudge(double steps)
    {
        var current = TryParse(Text, out var v) ? v : Math.Clamp(0, Min, Max);
        var next = Math.Clamp(Math.Round(current + steps * Step, Math.Max(Decimals, 0)), Min, Max);
        Text = Format(next);
        Box.CaretIndex = Box.Text.Length;
    }

    void OnUp(object sender, RoutedEventArgs e) => Nudge(1);
    void OnDown(object sender, RoutedEventArgs e) => Nudge(-1);

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var steps = e.Key switch { Key.Up => 1, Key.Down => -1, Key.PageUp => 5, Key.PageDown => -5, _ => 0 };
        if (steps == 0) return;
        Nudge(steps);
        e.Handled = true;
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Box.IsKeyboardFocused) return;
        Nudge(Math.Sign(e.Delta));
        e.Handled = true;
    }

    void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Корректное число приводим к единому виду (запятая), ошибочное оставляем как есть
        if (TryParse(Text, out var v) && v >= Min && v <= Max) Text = Format(v);
    }

    void UpdateState()
    {
        var hasError = Validation.GetHasError(this);
        var focused = Box.IsKeyboardFocused;
        Bd.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
            !IsEnabled ? "Brush.Fill.ControlDisabled" : focused ? "Brush.Fill.InputFocus" : IsMouseOver ? "Brush.Fill.ControlHover" : "Brush.Fill.Control");
        Bd.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, hasError ? "Brush.Text.Error" : "Brush.Stroke.Control");
        Line.Height = hasError || focused ? 2 : 1;
        Line.Visibility = IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        Line.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty,
            hasError ? "Brush.Text.Error" : focused ? "Brush.Accent.Default" : "Brush.Stroke.ControlStrong");
        Up.Visibility = Down.Visibility = IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        var valid = TryParse(Text, out var v);
        Up.IsEnabled = !valid || v < Max;
        Down.IsEnabled = !valid || v > Min;
        AutomationProperties.SetHelpText(Box, hasError ? Validation.GetErrors(this).FirstOrDefault()?.ErrorContent?.ToString() ?? "" : "");
    }
}

/// <summary>Подписка на изменение Validation.HasError (у него нет события в WPF).</summary>
static class DependencyPropertyDescriptorHelper
{
    public static void OnHasErrorChanged(DependencyObject target, Action callback) =>
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(Validation.HasErrorProperty, target.GetType())
            ?.AddValueChanged(target, (_, _) => callback());
}
