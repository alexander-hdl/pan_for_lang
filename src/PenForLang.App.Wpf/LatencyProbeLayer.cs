using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PenForLang.Core;

namespace PenForLang.App.Wpf;

/// <summary>
/// Слой замера задержки «перо → пиксель» камерой 240 fps (клавиша L; <c>docs/spikes/V0-hardware.md</c>, «Замер задержки»). Поверх белого
/// фона: крупная яркая отметка, которую программа ставит под перо при каждой пачке точек, и крупный счётчик кадров с временем QPC в углу
/// (обновляется на каждый <c>CompositionTarget.Rendering</c>). На видео смещение отметки от кончика пера в кадрах камеры (1 кадр = 4,17 мс)
/// даёт полную задержку; счётчик показывает такт кадров WPF и позволяет сверить время на видео.
/// </summary>
public sealed class LatencyProbeLayer : Grid
{
    private const double MarkerSizeDip = 56;

    private readonly IMonotonicClock _clock;
    private readonly Ellipse _marker;
    private readonly TextBlock _counter;
    private readonly TranslateTransform _markerOffset = new();
    private long _frames;
    private long _lastTicks;
    private bool _active;

    /// <summary>Создаёт слой (скрыт, пока не вызван <see cref="Activate"/>).</summary>
    /// <param name="clock">Часы QPC для времени в счётчике.</param>
    public LatencyProbeLayer(IMonotonicClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        ClipToBounds = true;

        var fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x1F, 0x5A));
        fill.Freeze();
        var outline = new SolidColorBrush(Colors.Black);
        outline.Freeze();
        _marker = new Ellipse
        {
            Width = MarkerSizeDip,
            Height = MarkerSizeDip,
            Fill = fill,
            Stroke = outline,
            StrokeThickness = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = _markerOffset,
            Visibility = Visibility.Hidden,
        };

        var back = new SolidColorBrush(Color.FromRgb(0xFF, 0xF2, 0x4D));
        back.Freeze();
        _counter = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 44,
            FontWeight = FontWeights.Bold,
            Foreground = outline,
            Background = back,
            Padding = new Thickness(18, 8, 18, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16),
            Text = "FRAME 0\nQPC 0.000 ms",
        };

        Children.Add(_marker);
        Children.Add(_counter);
    }

    /// <summary>Слой показан и считает кадры.</summary>
    public bool IsActive => _active;

    /// <summary>Сколько кадров <c>Rendering</c> прошло с <see cref="Activate"/>.</summary>
    public long Frames => _frames;

    /// <summary>Показывает слой и начинает считать кадры.</summary>
    public void Activate()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        _frames = 0;
        _lastTicks = _clock.Ticks;
        Visibility = Visibility.Visible;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Скрывает слой.</summary>
    public void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        CompositionTarget.Rendering -= OnRendering;
        Visibility = Visibility.Collapsed;
        _marker.Visibility = Visibility.Hidden;
    }

    /// <summary>Ставит центр отметки в точку (DIP относительно слоя). Вызывается из обработчика пачки точек — как можно раньше.</summary>
    public void MoveTo(double xDip, double yDip)
    {
        if (!_active)
        {
            return;
        }

        _markerOffset.X = xDip - (MarkerSizeDip / 2);
        _markerOffset.Y = yDip - (MarkerSizeDip / 2);
        if (_marker.Visibility != Visibility.Visible)
        {
            _marker.Visibility = Visibility.Visible;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = _clock.Ticks;
        _frames++;
        double qpcMs = now * 1000.0 / _clock.Frequency;
        double dtMs = (now - _lastTicks) * 1000.0 / _clock.Frequency;
        _lastTicks = now;
        _counter.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"FRAME {_frames}\nQPC {qpcMs:0.000} ms\ndt {dtMs:0.0} ms");
    }
}
