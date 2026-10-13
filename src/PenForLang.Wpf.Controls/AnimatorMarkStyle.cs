namespace PenForLang.Wpf.Controls;

/// <summary>
/// Размеры меток аниматора (V0.6, ревью F5): стрелки направления, номера штрихов, точка старта и «перо». Метка для пера и детского
/// глаза не может быть меньше физического минимума, поэтому каждый размер — максимум из доли x-height и минимума в миллиметрах
/// (линейка в мм, ARCHITECTURE.md §7.3). Значения по умолчанию — константы стиля; применяются слоем <see cref="GlyphAnimatorLayer"/>.
/// </summary>
public sealed record AnimatorMarkStyle
{
    /// <summary>Длина стрелки не меньше, мм.</summary>
    public double ArrowMinMm { get; init; } = 4.5;

    /// <summary>Длина стрелки, доля x-height (если больше минимума).</summary>
    public double ArrowRelative { get; init; } = 0.2;

    /// <summary>Смещение стрелки вбок от линии штриха (от края линии), мм.</summary>
    public double ArrowSideOffsetMm { get; init; } = 2.5;

    /// <summary>Диаметр кружка номера не меньше, мм.</summary>
    public double BadgeMinDiameterMm { get; init; } = 6.5;

    /// <summary>Радиус кружка номера, доля x-height (если больше минимума).</summary>
    public double BadgeRelative { get; init; } = 0.085;

    /// <summary>Высота цифры номера не меньше, мм.</summary>
    public double DigitMinMm { get; init; } = 4.0;

    /// <summary>Диаметр зелёной точки старта не меньше, мм.</summary>
    public double StartDotMinDiameterMm { get; init; } = 3.0;

    /// <summary>Радиус точки старта, доля x-height.</summary>
    public double StartDotRelative { get; init; } = 0.05;

    /// <summary>Диаметр кружка «пера» не меньше, мм.</summary>
    public double PenMinDiameterMm { get; init; } = 4.0;

    /// <summary>Радиус кружка «пера», доля x-height.</summary>
    public double PenRelative { get; init; } = 0.07;

    /// <summary>Считает размеры меток в DIP для строки с заданным x-height и масштабом.</summary>
    /// <param name="xHeightDip">x-height, DIP.</param>
    /// <param name="dipPerMm">DIP в одном миллиметре (<c>PxPerMm / dpiScale</c>).</param>
    public AnimatorMarkSizes Resolve(double xHeightDip, double dipPerMm)
    {
        double em = Math.Max(DigitMinMm / 0.7 * dipPerMm, BadgeRelative * xHeightDip * 1.2);
        return new AnimatorMarkSizes(
            ArrowLength: Math.Max(ArrowRelative * xHeightDip, ArrowMinMm * dipPerMm),
            ArrowSideOffset: ArrowSideOffsetMm * dipPerMm,
            BadgeRadius: Math.Max(Math.Max(BadgeMinDiameterMm * dipPerMm / 2, BadgeRelative * xHeightDip), em * 0.62),
            DigitEm: em,
            StartDotRadius: Math.Max(StartDotRelative * xHeightDip, StartDotMinDiameterMm * dipPerMm / 2),
            PenRadius: Math.Max(PenRelative * xHeightDip, PenMinDiameterMm * dipPerMm / 2));
    }
}

/// <summary>Размеры меток аниматора в DIP.</summary>
/// <param name="ArrowLength">Длина стрелки.</param>
/// <param name="ArrowSideOffset">Смещение стрелки вбок от края линии.</param>
/// <param name="BadgeRadius">Радиус кружка номера.</param>
/// <param name="DigitEm">Кегль цифры номера (высота цифры ≈ 0,7 кегля).</param>
/// <param name="StartDotRadius">Радиус точки старта.</param>
/// <param name="PenRadius">Радиус кружка «пера».</param>
public readonly record struct AnimatorMarkSizes(
    double ArrowLength,
    double ArrowSideOffset,
    double BadgeRadius,
    double DigitEm,
    double StartDotRadius,
    double PenRadius);
