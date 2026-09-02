using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>Полоса прогресса как данные: текст, доля, «бегущая» или нет.
///
/// Одна и та же для установки игры и для обновления лаунчера — раньше это
/// были два одинаковых набора свойств в двух вьюмоделях, и правка в одном
/// не доезжала до другого.</summary>
public sealed class ProgressState : ObservableObject
{
    private double _value;
    public double Value { get => _value; private set => Set(ref _value, value); }

    private bool _indeterminate;
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }

    private string _text = "";
    public string Text { get => _text; private set => Set(ref _text, value); }

    /// <summary>Начало: доли ещё нет, полоса бежит.</summary>
    public void Begin(string text = "Подготовка…")
    {
        Value = 0;
        Indeterminate = true;
        Text = text;
    }

    public void Report(InstallProgress p, string? textOverride = null)
    {
        Text = textOverride ?? p.Describe();
        if (p.Fraction is { } fraction)
        {
            Indeterminate = false;
            Value = fraction * 100;
        }
        else
        {
            Indeterminate = true;
        }
    }

    public void Clear() => Text = "";
}
