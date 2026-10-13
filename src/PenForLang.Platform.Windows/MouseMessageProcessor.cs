using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Мышь как перо (режим разработчика): превращает оконные сообщения мыши в <see cref="PenSample"/> и применяет то же правило
/// владения, что и для пера (<see cref="PointerOwnership"/>). Чистая часть <see cref="MousePenSource"/>: экранные координаты
/// и время подаёт вызывающий, поэтому класс тестируется без окна.
/// </summary>
/// <remarks>
/// Давление: <see cref="ContactPressure"/> в контакте (левая кнопка нажата), 0 при наведении. Мышь одна — ей присвоен
/// постоянный <see cref="MousePointerId"/>.
/// </remarks>
internal sealed class MouseMessageProcessor
{
    /// <summary>Постоянное давление мыши в контакте (ARCHITECTURE.md §7.1: «давление 0,5»).</summary>
    public const float ContactPressure = 0.5f;

    /// <summary>Идентификатор «указателя» мыши в <see cref="PenSample.PointerId"/>.</summary>
    public const uint MousePointerId = 0;

    private readonly PointerOwnership _ownership = new(1, 1);
    private PenSample _last;
    private PenSurface? _surface;

    /// <summary>Экранный прямоугольник холста задан.</summary>
    public bool HasSurface => _surface is not null;

    /// <summary>Мышь сейчас ведёт штрих на холсте.</summary>
    public bool IsOwned => _ownership.IsOwned(MousePointerId);

    /// <summary>Задаёт экранный прямоугольник холста (физические px).</summary>
    public void SetSurface(PenSurface surface)
    {
        _ownership.SetSurface(surface);
        _surface = surface;
    }

    /// <summary>Снимает владение (остановка источника).</summary>
    public void Reset() => _ownership.Reset();

    /// <summary>Сообщение мыши, интересное источнику.</summary>
    public static bool IsMouseMessage(uint message) =>
        message is NativeConstants.WmMouseMove
            or NativeConstants.WmLButtonDown
            or NativeConstants.WmLButtonUp
            or NativeConstants.WmCaptureChanged;

    /// <summary>Обрабатывает сообщение.</summary>
    /// <param name="message">Код сообщения мыши.</param>
    /// <param name="wParam">wParam (для <c>WM_MOUSEMOVE</c> — маска нажатых кнопок <c>MK_*</c>).</param>
    /// <param name="screenXPx">Экранный X курсора, физические px.</param>
    /// <param name="screenYPx">Экранный Y курсора, физические px.</param>
    /// <param name="absoluteTimeUs">Абсолютное монотонное время сообщения, мкс.</param>
    /// <param name="sample">Точка для выдачи подписчикам, если сообщение принято (или закрыт штрих).</param>
    /// <param name="hasSample"><paramref name="sample"/> заполнена (сообщение принято либо штрих закрыт потерей захвата).</param>
    /// <returns><c>true</c> — сообщение принадлежит холсту (пометить обработанным).</returns>
    public bool TryProcess(uint message, nint wParam, double screenXPx, double screenYPx, long absoluteTimeUs, out PenSample sample, out bool hasSample)
    {
        sample = default;
        hasSample = false;
        if (_surface is not PenSurface surface)
        {
            return false;
        }

        if (message == NativeConstants.WmCaptureChanged)
        {
            // Захват потерян посреди штриха: закрываем его.
            if (!_ownership.IsOwned(MousePointerId))
            {
                return false;
            }

            _ownership.Release(MousePointerId);
            sample = _last with { Phase = PenPhase.Up, Pressure = 0f, TimeUs = Math.Max(absoluteTimeUs, _last.TimeUs) };
            hasSample = true;
            return false;
        }

        PenPhase phase;
        switch (message)
        {
            case NativeConstants.WmLButtonDown:
                phase = PenPhase.Down;
                break;
            case NativeConstants.WmLButtonUp:
                phase = PenPhase.Up;
                break;
            case NativeConstants.WmMouseMove:
                phase = ((uint)wParam & NativeConstants.MkLButton) != 0 ? PenPhase.Move : PenPhase.Hover;
                break;
            default:
                return false;
        }

        var raw = new PenSample(
            TimeUs: absoluteTimeUs,
            X: screenXPx - surface.OriginXPx,
            Y: screenYPx - surface.OriginYPx,
            Pressure: phase == PenPhase.Hover || phase == PenPhase.Up ? 0f : ContactPressure,
            TiltXDeg: 0f,
            TiltYDeg: 0f,
            Phase: phase,
            Buttons: PenButtons.None,
            Flags: SampleFlags.None,
            PointerId: MousePointerId);

        if (!_ownership.TryProcess(raw, out sample))
        {
            return false;
        }

        _last = sample;
        hasSample = true;
        return true;
    }
}
