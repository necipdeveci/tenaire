using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;

namespace Tenaire;

/// <summary>Virgul kabul eden, ok tuslari ve tekerlekle adimlanan sayi kutusu.</summary>
sealed class NumberBox : TextBox
{
    decimal _value;

    public decimal Minimum { get; set; }
    public decimal Maximum { get; set; } = 100;
    public decimal Step { get; set; } = 1;
    public int Decimals { get; set; }

    /// <summary>Deger gercekten degistiginde calisir.</summary>
    public event Action? ValueChanged;

    public decimal Value
    {
        get
        {
            Commit();
            return _value;
        }
        set => Apply(value);
    }

    void Apply(decimal value)
    {
        var clamped = Math.Round(Math.Clamp(value, Minimum, Maximum), Decimals);
        var changed = clamped != _value;
        _value = clamped;

        var text = clamped.ToString("F" + Decimals, CultureInfo.InvariantCulture).Replace('.', ',');
        if (Text != text) Text = text;
        if (changed) ValueChanged?.Invoke();
    }

    void Commit()
    {
        var parsed = decimal.TryParse(Text.Replace(',', '.').Replace('−', '-'), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var value);
        Apply(parsed ? value : _value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                SelectAll();
                e.Handled = true;
                break;
            case Key.Up:
                Apply(Value + Step);
                e.Handled = true;
                break;
            case Key.Down:
                Apply(Value - Step);
                e.Handled = true;
                break;
        }
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        Apply(Value + Math.Sign(e.Delta) * Step);
        e.Handled = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Commit();
    }
}
