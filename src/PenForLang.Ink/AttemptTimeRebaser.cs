using PenForLang.Core;

namespace PenForLang.Ink;

/// <summary>
/// Единственное место пересчёта абсолютного монотонного времени источника (<see cref="PenTimeBase.MonotonicAbsolute"/>)
/// во время «мкс от начала задания» (ARCHITECTURE.md §3.1). Контроллер записи попытки создаёт экземпляр в момент старта
/// задания и пропускает через него каждую пачку точек и каждое событие.
/// </summary>
/// <remarks>
/// Абсолютные значения и начало задания берутся в мкс (каждое — через <see cref="MonotonicTime.ToMicroseconds"/>, без переполнения
/// даже после недель аптайма); разность двух значений, округлённых вниз, отличается от точной не более чем на 1 мкс.
/// </remarks>
public sealed class AttemptTimeRebaser
{
    /// <summary>Создаёт пересчётчик с заданным началом задания.</summary>
    /// <param name="originAbsoluteUs">Абсолютное монотонное время начала задания, мкс; не отрицательно.</param>
    /// <exception cref="ArgumentOutOfRangeException">Начало отрицательно.</exception>
    public AttemptTimeRebaser(long originAbsoluteUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originAbsoluteUs);
        OriginAbsoluteUs = originAbsoluteUs;
    }

    /// <summary>Абсолютное монотонное время начала задания, мкс.</summary>
    public long OriginAbsoluteUs { get; }

    /// <summary>Создаёт пересчётчик с началом задания «сейчас» по монотонным часам.</summary>
    public static AttemptTimeRebaser Start(IMonotonicClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new AttemptTimeRebaser(MonotonicTime.ToMicroseconds(clock.Ticks, clock.Frequency));
    }

    /// <summary>Абсолютное время в мкс, соответствующее тикам часов с частотой <paramref name="frequency"/>. Используется источниками.</summary>
    public static long ToAbsoluteMicroseconds(long ticks, long frequency) => MonotonicTime.ToMicroseconds(ticks, frequency);

    /// <summary>Пересчитывает абсолютное время в относительное; результат отрицателен, если момент раньше начала задания.</summary>
    public long ToTaskTime(long absoluteUs) => absoluteUs - OriginAbsoluteUs;

    /// <summary>Пересчитывает точку; <c>false</c>, если точка раньше начала задания (её следует отбросить).</summary>
    public bool TryRebase(in PenSample sample, out PenSample rebased)
    {
        long time = ToTaskTime(sample.TimeUs);
        rebased = sample with { TimeUs = Math.Max(0, time) };
        return time >= 0;
    }

    /// <summary>
    /// Пересчитывает пачку; точки раньше начала задания пропускаются.
    /// </summary>
    /// <param name="source">Пачка источника.</param>
    /// <param name="destination">Куда писать результат; не короче <paramref name="source"/>.</param>
    /// <returns>Число записанных точек.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> короче <paramref name="source"/>.</exception>
    public int Rebase(ReadOnlySpan<PenSample> source, Span<PenSample> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination is shorter than source.", nameof(destination));
        }

        int count = 0;
        foreach (PenSample sample in source)
        {
            if (TryRebase(sample, out PenSample rebased))
            {
                destination[count++] = rebased;
            }
        }

        return count;
    }
}
