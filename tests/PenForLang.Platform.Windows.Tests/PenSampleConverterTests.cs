using PenForLang.Ink;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows.Tests;

public class PenSampleConverterTests
{
    [Theory]
    [InlineData(0x00010000u | 0x2 | 0x4, PenPhase.Down)] // DOWN | INRANGE | INCONTACT
    [InlineData(0x00020000u | 0x2 | 0x4, PenPhase.Move)] // UPDATE | INRANGE | INCONTACT
    [InlineData(0x00040000u | 0x2, PenPhase.Up)] // UP | INRANGE
    [InlineData(0x00020000u | 0x2, PenPhase.Hover)] // UPDATE | INRANGE, без контакта
    [InlineData(0x00020000u, PenPhase.Hover)] // вышло из зоны (нет INRANGE)
    public void Phase_follows_the_pointer_flags(uint flags, PenPhase expected)
    {
        Assert.Equal(expected, PenSampleConverter.ToPhase(flags));
    }

    [Theory]
    [InlineData(0u, 0f)]
    [InlineData(512u, 0.5f)]
    [InlineData(1024u, 1f)]
    [InlineData(5000u, 1f)] // вне диапазона обрезается
    public void Pressure_is_normalised_from_0_1024_to_0_1(uint raw, float expected)
    {
        Assert.Equal(expected, PenSampleConverter.ToPressure(NativeConstants.PenMaskPressure, raw), precision: 5);
    }

    [Fact]
    public void Pressure_is_zero_when_the_device_does_not_report_it()
    {
        Assert.Equal(0f, PenSampleConverter.ToPressure(penMask: 0, pressure: 700));
    }

    [Theory]
    [InlineData(NativeConstants.PenMaskTiltX, 30, 30f)]
    [InlineData(NativeConstants.PenMaskTiltX, -90, -90f)]
    [InlineData(NativeConstants.PenMaskTiltX, 200, 90f)]
    [InlineData(0u, 30, 0f)] // маска не заявляет наклон
    [InlineData(NativeConstants.PenMaskTiltY, 30, 0f)] // заявлен другой наклон
    public void Tilt_uses_the_mask_and_is_clamped(uint mask, int raw, float expected)
    {
        Assert.Equal(expected, PenSampleConverter.ToTilt(mask, NativeConstants.PenMaskTiltX, raw));
    }

    [Theory]
    [InlineData(0u, PenButtons.None)]
    [InlineData(NativeConstants.PenFlagBarrel, PenButtons.Barrel)]
    [InlineData(NativeConstants.PenFlagInverted, PenButtons.Eraser)]
    [InlineData(NativeConstants.PenFlagEraser, PenButtons.Eraser)]
    [InlineData(NativeConstants.PenFlagBarrel | NativeConstants.PenFlagInverted, PenButtons.Barrel | PenButtons.Eraser)]
    public void Buttons_map_barrel_and_eraser(uint penFlags, PenButtons expected)
    {
        Assert.Equal(expected, PenSampleConverter.ToButtons(penFlags));
    }

    [Fact]
    public void Time_conversion_does_not_overflow_for_a_month_of_uptime()
    {
        // 30 суток на 10 МГц = 2,592e13 тиков; тики * 1e6 переполнили бы long.
        ulong ticks = 30UL * 24 * 3600 * 10_000_000 + 3;
        long us = PenSampleConverter.ToAbsoluteMicroseconds(ticks, 10_000_000, fallbackTicks: 0, out bool fallback);

        Assert.False(fallback);
        Assert.Equal((30L * 24 * 3600 * 1_000_000) + 0, us); // 3 тика = 0,3 мкс, округление вниз
    }

    [Fact]
    public void Time_conversion_handles_a_non_decimal_frequency()
    {
        // 3,0 МГц: 7 тиков = 2,333 мкс -> 2.
        long us = PenSampleConverter.ToAbsoluteMicroseconds(7, 3_000_000, fallbackTicks: 0, out _);
        Assert.Equal(2, us);
    }

    [Fact]
    public void Convert_subtracts_the_canvas_origin_and_does_not_touch_dpi()
    {
        NativePointerPenInfo info = Pen.Down(4, x: 1234, y: 777, perf: 20_000_000, pressure: 256);

        PenSample sample = PenSampleConverter.Convert(in info, 1000.5, 500, 10_000_000, fallbackTicks: 0, coalesced: false);

        Assert.Equal(233.5, sample.X);
        Assert.Equal(277.0, sample.Y);
        Assert.Equal(2_000_000, sample.TimeUs);
        Assert.Equal(0.25f, sample.Pressure);
        Assert.Equal(4u, sample.PointerId);
        Assert.Equal(PenPhase.Down, sample.Phase);
    }
}
