using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Чистые функции перевода <c>POINTER_PEN_INFO</c> в <see cref="PenSample"/> (ARCHITECTURE.md §3.1, §7.1).
/// Не обращаются к ОС и часам — тестируются на заполненных вручную структурах.
/// </summary>
internal static class PenSampleConverter
{
    /// <summary>Фаза точки по <c>POINTER_FLAGS</c>: DOWN → Down, UP → Up, иначе контакт → Move, иначе Hover.</summary>
    public static PenPhase ToPhase(uint pointerFlags)
    {
        if ((pointerFlags & NativeConstants.PointerFlagDown) != 0)
        {
            return PenPhase.Down;
        }

        if ((pointerFlags & NativeConstants.PointerFlagUp) != 0)
        {
            return PenPhase.Up;
        }

        return (pointerFlags & NativeConstants.PointerFlagInContact) != 0 ? PenPhase.Move : PenPhase.Hover;
    }

    /// <summary>Кнопки по <c>PEN_FLAGS</c>: BARREL → Barrel; INVERTED и ERASER → Eraser.</summary>
    public static PenButtons ToButtons(uint penFlags)
    {
        PenButtons buttons = PenButtons.None;
        if ((penFlags & NativeConstants.PenFlagBarrel) != 0)
        {
            buttons |= PenButtons.Barrel;
        }

        if ((penFlags & (NativeConstants.PenFlagInverted | NativeConstants.PenFlagEraser)) != 0)
        {
            buttons |= PenButtons.Eraser;
        }

        return buttons;
    }

    /// <summary>
    /// Нормированное давление 0..1024 → 0..1. Если устройство давление не сообщает (нет <c>PEN_MASK_PRESSURE</c>), результат 0;
    /// значения вне диапазона обрезаются.
    /// </summary>
    public static float ToPressure(uint penMask, uint pressure)
    {
        if ((penMask & NativeConstants.PenMaskPressure) == 0)
        {
            return 0f;
        }

        return Math.Clamp(pressure / NativeConstants.PenPressureMax, 0f, 1f);
    }

    /// <summary>Наклон в градусах (−90..+90); 0, если устройство его не сообщает (нет соответствующего бита маски).</summary>
    public static float ToTilt(uint penMask, uint maskBit, int tilt) =>
        (penMask & maskBit) == 0 ? 0f : Math.Clamp(tilt, -90, 90);

    /// <summary>
    /// <c>PerformanceCount</c> (тики QPC) → абсолютное монотонное время в мкс (<see cref="PenTimeBase.MonotonicAbsolute"/>).
    /// </summary>
    /// <param name="performanceCount">Поле <c>POINTER_INFO.PerformanceCount</c>; 0 — ОС метку не дала.</param>
    /// <param name="frequency">Частота QPC, Гц.</param>
    /// <param name="fallbackTimeUs">Абсолютное время в мкс, подставляемое, если метки нет (вычисляет вызывающий, F4).</param>
    /// <param name="usedFallback">Метки не было, время взято из <paramref name="fallbackTimeUs"/>.</param>
    public static long ToAbsoluteMicroseconds(ulong performanceCount, long frequency, long fallbackTimeUs, out bool usedFallback)
    {
        usedFallback = performanceCount == 0 || performanceCount > long.MaxValue;
        return usedFallback ? fallbackTimeUs : MonotonicTime.ToMicroseconds((long)performanceCount, frequency);
    }

    /// <summary>
    /// Полное преобразование одной записи истории в точку поверхности.
    /// Координаты: <c>ptPixelLocation</c> (физические экранные px) минус начало холста — без DPI (A12).
    /// </summary>
    /// <param name="info">Запись из <c>GetPointerPenInfoHistory</c>.</param>
    /// <param name="surfaceOriginXPx">Левый край холста на экране, физические px.</param>
    /// <param name="surfaceOriginYPx">Верхний край холста на экране, физические px.</param>
    /// <param name="frequency">Частота QPC, Гц.</param>
    /// <param name="fallbackTimeUs">Время в мкс на случай отсутствия метки (см. <see cref="PenPointerMessageProcessor"/>, F4).</param>
    /// <param name="coalesced">Запись — не новейшая в сообщении (история), помечается <see cref="SampleFlags.Coalesced"/>.</param>
    public static PenSample Convert(
        in NativePointerPenInfo info,
        double surfaceOriginXPx,
        double surfaceOriginYPx,
        long frequency,
        long fallbackTimeUs,
        bool coalesced)
    {
        long timeUs = ToAbsoluteMicroseconds(info.PointerInfo.PerformanceCount, frequency, fallbackTimeUs, out bool usedFallback);

        SampleFlags flags = SampleFlags.None;
        if (coalesced)
        {
            flags |= SampleFlags.Coalesced;
        }

        if (usedFallback)
        {
            flags |= SampleFlags.InterpolatedTime;
        }

        return new PenSample(
            TimeUs: timeUs,
            X: info.PointerInfo.PtPixelLocation.X - surfaceOriginXPx,
            Y: info.PointerInfo.PtPixelLocation.Y - surfaceOriginYPx,
            Pressure: ToPressure(info.PenMask, info.Pressure),
            TiltXDeg: ToTilt(info.PenMask, NativeConstants.PenMaskTiltX, info.TiltX),
            TiltYDeg: ToTilt(info.PenMask, NativeConstants.PenMaskTiltY, info.TiltY),
            Phase: ToPhase(info.PointerInfo.PointerFlags),
            Buttons: ToButtons(info.PenFlags),
            Flags: flags,
            PointerId: info.PointerInfo.PointerId);
    }
}
