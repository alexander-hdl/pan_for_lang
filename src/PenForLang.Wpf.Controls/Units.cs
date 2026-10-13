namespace PenForLang.Wpf.Controls;

/// <summary>
/// Перевод единиц для визуального слоя. Линейки задаются в миллиметрах (ARCHITECTURE.md §7.3),
/// WPF рисует в DIP (1 DIP = 1/96 дюйма).
/// </summary>
public static class Units
{
    /// <summary>Миллиметров в дюйме.</summary>
    public const double MillimetersPerInch = 25.4;

    /// <summary>DIP в дюйме.</summary>
    public const double DipsPerInch = 96.0;

    /// <summary>Миллиметры в DIP.</summary>
    public static double MmToDip(double millimeters) => millimeters * DipsPerInch / MillimetersPerInch;

    /// <summary>DIP в миллиметры.</summary>
    public static double DipToMm(double dips) => dips * MillimetersPerInch / DipsPerInch;
}
