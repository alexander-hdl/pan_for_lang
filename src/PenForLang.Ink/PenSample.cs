namespace PenForLang.Ink;

/// <summary>
/// Одна точка пера в системе координат поверхности письма (ARCHITECTURE.md §3.1).
/// </summary>
/// <param name="X">Физические пиксели от левого верхнего угла холста.</param>
/// <param name="Y">Физические пиксели от левого верхнего угла холста.</param>
/// <param name="Pressure">Давление, 0..1.</param>
/// <param name="TimeMicros">Время в микросекундах от начала задания.</param>
public readonly record struct PenSample(double X, double Y, float Pressure, long TimeMicros);
