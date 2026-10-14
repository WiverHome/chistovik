using System.Collections;
using System.ComponentModel;
using System.Globalization;
using ShablonRabot.App.Infrastructure;

namespace ShablonRabot.App.ViewModels;

public sealed record Choice(object Value, string Label)
{
    public override string ToString() => Label;
}

public enum InfoKind { Informational, Success, Warning, Error }

public sealed class InfoBarViewModel
{
    public InfoKind Kind { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
    public Action? Close { get; set; }
    public bool Closable { get; init; } = true;
    /// <summary>Инфобар «Отменить»: исчезает при следующем действии пользователя.</summary>
    public bool IsUndo { get; init; }
    /// <summary>«Исправьте ошибки…»: обновляется и исчезает вместе с ошибками.</summary>
    public bool IsValidation { get; init; }
}

/// <summary>Числовое правило: текст поля + последнее корректное значение + ошибка.</summary>
public sealed class NumberRule : ObservableObject, INotifyDataErrorInfo
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    readonly double _min, _max;
    readonly bool _integer;
    readonly string _errorText;
    string _text = "";
    string? _error;
    bool _loading;

    public NumberRule(double min, double max, bool integer, string errorText)
    {
        (_min, _max, _integer, _errorText) = (min, max, integer, errorText);
    }

    public event EventHandler? Changed;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public double Value { get; private set; }
    public string? Error => _error;
    public bool HasErrors => _error != null;

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            var ok = Controls.NumberBox.TryParse(value, out var v) && v >= _min && v <= _max
                     && (!_integer || Math.Abs(v - Math.Round(v)) < 1e-9);
            SetError(ok ? null : _errorText);
            if (ok && Math.Abs(v - Value) > 1e-9)
            {
                Value = v;
                OnPropertyChanged(nameof(Value));
            }
            if (!_loading) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Load(double value)
    {
        _loading = true;
        Value = value;
        Text = Format(value);
        SetError(null);
        _loading = false;
        OnPropertyChanged(nameof(Value));
    }

    public static string Format(double v) => v.ToString("0.##", Ru);

    void SetError(string? error)
    {
        if (_error == error) return;
        _error = error;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Text)));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasErrors));
    }

    public IEnumerable GetErrors(string? propertyName) =>
        propertyName == nameof(Text) && _error != null ? new[] { _error } : Array.Empty<string>();
}

/// <summary>Состояние одного правила относительно эталона (ГОСТ или исходного профиля).</summary>
public sealed class RuleDiff : ObservableObject
{
    bool _isChanged;
    string _text = "";

    public RuleDiff(string key, string name) { Key = key; Name = name; }

    public string Key { get; }
    public string Name { get; }
    public bool IsChanged { get => _isChanged; set => Set(ref _isChanged, value); }
    public string ChangedText { get => _text; set => Set(ref _text, value); }
    public string RevertName => $"Вернуть значение по ГОСТ: {Name}";
}
