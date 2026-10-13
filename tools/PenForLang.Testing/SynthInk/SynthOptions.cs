namespace PenForLang.Testing.SynthInk;

/// <summary>
/// Пределы вариаций «детской» рукописи для <see cref="SynthInkGenerator"/> (ARCHITECTURE.md §10.3, шаг 2).
/// Синтетика проверяет регрессию реализации, а не точность на детях (A08).
/// </summary>
public sealed record SynthOptions
{
    /// <summary>Частота дискретизации пера, Гц.</summary>
    public double SampleRateHz { get; init; } = 200.0;

    /// <summary>Наименьший масштаб буквы (x-height рукописи / x-height линейки).</summary>
    public double ScaleMin { get; init; } = 0.8;

    /// <summary>Наибольший масштаб буквы.</summary>
    public double ScaleMax { get; init; } = 1.3;

    /// <summary>Наклон письма относительно эталона: равномерно в ±этого значения, градусы.</summary>
    public double SlantRangeDeg { get; init; } = 15.0;

    /// <summary>Поворот строки: равномерно в ±этого значения, градусы.</summary>
    public double RotationRangeDeg { get; init; } = 3.0;

    /// <summary>Положение начала буквы по u: равномерно в [-OffsetU, +OffsetU] вокруг 1, x-height.</summary>
    public double OffsetU { get; init; } = 1.0;

    /// <summary>Дрейф baseline: равномерно в ±этого значения, x-height.</summary>
    public double BaselineDrift { get; init; } = 0.06;

    /// <summary>Смещение каждого штриха отдельно (гауссово σ), x-height.</summary>
    public double StrokeOffsetSigma { get; init; } = 0.02;

    /// <summary>Амплитуда медленной «волны» вдоль штриха (равномерно до этого значения), x-height.</summary>
    public double WobbleAmplitude { get; init; } = 0.04;

    /// <summary>Тремор: независимый гауссов шум на точку (σ), x-height.</summary>
    public double TremorSigma { get; init; } = 0.005;

    /// <summary>Множитель скорости: 1 — как у анимации; меньше — медленнее (время растягивается).</summary>
    public double SpeedMin { get; init; } = 0.7;

    /// <summary>Наибольший множитель скорости.</summary>
    public double SpeedMax { get; init; } = 1.5;

    /// <summary>Джиттер временных меток, мкс (равномерно ±).</summary>
    public double TimeJitterUs { get; init; } = 400.0;

    /// <summary>Миллиметров в единице линейки (x-height в мм) для <c>RulerInk.MmPerUnit</c>.</summary>
    public double MmPerUnit { get; init; } = 10.0;
}
