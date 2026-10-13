namespace PenForLang.Core;

/// <summary>
/// Порт монотонных часов высокого разрешения (QPC в Windows). Не связан с календарём и не идёт назад;
/// начало отсчёта произвольно (например, загрузка ОС), поэтому значения имеют смысл только как разности.
/// Реализации: <c>QpcMonotonicClock</c> в <c>Platform.Windows</c>, <c>FakeMonotonicClock</c> в <c>PenForLang.Testing</c>.
/// Перевод в микросекунды — <see cref="MonotonicTime"/>.
/// </summary>
public interface IMonotonicClock
{
    /// <summary>Тиков в секунде; постоянна и положительна.</summary>
    long Frequency { get; }

    /// <summary>Текущее значение счётчика в тиках; не убывает.</summary>
    long Ticks { get; }
}
