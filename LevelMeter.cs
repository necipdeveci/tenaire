using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Tenaire;

/// <summary>dBFS olcekli seviye cubugu: anlik seviye, tepe tutma cizgisi ve altinda dB olcegi.</summary>
sealed class LevelMeter : FrameworkElement
{
    const double FloorDb = -60;
    const double Decay = 0.80;
    const int HoldTicks = 30; // 50 ms'lik zamanlayiciyla 1,5 sn
    const double TrackHeight = 12;
    const double TrackRadius = 4;

    static readonly int[] ScaleMarks = { -48, -36, -24, -12, -6, -3, 0 };
    static readonly int[] LabeledMarks = { -48, -24, -12, -6, 0 };

    static readonly Brush Track = Solid("#101216");
    static readonly Brush Green = Solid("#3DD68C");
    static readonly Brush Amber = Solid("#F5B83D");
    static readonly Brush Red = Solid("#EC4B51");
    static readonly Brush Hold = Solid("#EEF0F4");
    static readonly Brush ScaleText = Solid("#7F8794");
    static readonly Brush TickLine = Solid("#171A20");
    static readonly Typeface ScaleFace = new("Segoe UI");

    double _level, _held;
    double _levelRatio, _heldRatio;
    int _holdRemaining;

    static Brush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Son 1,5 saniyedeki en yuksek seviye (dBFS). Sinyal yoksa null.</summary>
    public double? HeldDb => _held > 0.00001 ? 20 * Math.Log10(_held) : null;

    static double ToRatio(double db) => Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

    static double LevelToRatio(double level) => level <= 0 ? 0 : ToRatio(20 * Math.Log10(level));

    /// <param name="peak">Kazanc uygulanmis tepe degeri (1 = 0 dBFS).</param>
    public void Update(double peak)
    {
        _level = Math.Max(peak, _level * Decay);

        if (peak >= _held)
        {
            _held = peak;
            _holdRemaining = HoldTicks;
        }
        else if (_holdRemaining > 0)
        {
            _holdRemaining--;
        }
        else
        {
            _held *= Decay;
        }

        var levelRatio = LevelToRatio(_level);
        var heldRatio = LevelToRatio(_held);
        if (Math.Abs(levelRatio - _levelRatio) < 0.003 && Math.Abs(heldRatio - _heldRatio) < 0.003) return;
        _levelRatio = levelRatio;
        _heldRatio = heldRatio;
        InvalidateVisual();
    }

    public void Reset()
    {
        _level = _held = _levelRatio = _heldRatio = 0;
        _holdRemaining = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var track = new Rect(0, 0, width, TrackHeight);
        context.DrawRoundedRectangle(Track, null, track, TrackRadius, TrackRadius);

        context.PushClip(new RectangleGeometry(track, TrackRadius, TrackRadius));
        var filled = width * _levelRatio;
        var warnX = width * ToRatio(-12);
        var hotX = width * ToRatio(-3);
        context.DrawRectangle(Green, null, new Rect(0, 0, Math.Min(filled, warnX), TrackHeight));
        if (filled > warnX) context.DrawRectangle(Amber, null, new Rect(warnX, 0, Math.Min(filled, hotX) - warnX, TrackHeight));
        if (filled > hotX) context.DrawRectangle(Red, null, new Rect(hotX, 0, filled - hotX, TrackHeight));

        foreach (var mark in ScaleMarks)
            context.DrawRectangle(TickLine, null, new Rect(width * ToRatio(mark), 0, 1, TrackHeight));

        if (_heldRatio > 0)
            context.DrawRectangle(_held > 1 ? Red : Hold, null, new Rect(Math.Min(width * _heldRatio, width - 2), 0, 2, TrackHeight));
        context.Pop();

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var mark in LabeledMarks)
        {
            var text = new FormattedText(mark.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, ScaleFace, 9.5, ScaleText, pixelsPerDip);
            var x = Math.Clamp(width * ToRatio(mark) - text.Width / 2, 0, width - text.Width);
            context.DrawText(text, new Point(x, TrackHeight + 2));
        }
    }
}
