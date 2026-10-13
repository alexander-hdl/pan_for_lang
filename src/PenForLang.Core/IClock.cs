namespace PenForLang.Core;

/// <summary>
/// Порт времени. Домен никогда не читает системные часы напрямую (R2): время приходит через этот интерфейс.
/// Реализации: <c>SystemClock</c> в корне композиции, <c>FakeClock</c> в <c>PenForLang.Testing</c>.
/// </summary>
public interface IClock
{
    /// <summary>Текущий момент в UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
