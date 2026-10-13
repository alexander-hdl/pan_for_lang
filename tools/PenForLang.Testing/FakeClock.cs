using PenForLang.Core;

namespace PenForLang.Testing;

/// <summary>
/// Управляемые часы для тестов: время идёт только при вызове <see cref="Advance"/> или <see cref="Set"/>.
/// </summary>
public sealed class FakeClock : IClock
{
    /// <summary>Момент по умолчанию — фиксированный, чтобы тесты не зависели от даты запуска.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public FakeClock(DateTimeOffset? start = null)
    {
        UtcNow = (start ?? DefaultStart).ToUniversalTime();
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; }

    /// <summary>Сдвигает время вперёд.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Сдвиг отрицателен.</exception>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        UtcNow += delta;
    }

    /// <summary>Устанавливает время (допускает перемотку назад — осознанно, для тестов).</summary>
    public void Set(DateTimeOffset value) => UtcNow = value.ToUniversalTime();
}
