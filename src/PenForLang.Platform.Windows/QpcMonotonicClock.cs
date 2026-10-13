using System.Runtime.InteropServices;
using PenForLang.Core;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Монотонные часы на <c>QueryPerformanceCounter</c>/<c>QueryPerformanceFrequency</c> (ARCHITECTURE.md §3.1, §7.1).
/// Источники пера переводят тики в мкс через <see cref="MonotonicTime"/> (без переполнения), а пересчёт «от начала задания»
/// выполняет <c>AttemptTimeRebaser</c>. P/Invoke разрешён только в этой сборке (R4).
/// </summary>
public sealed partial class QpcMonotonicClock : IMonotonicClock
{
    /// <summary>Создаёт часы; частота считывается один раз (она постоянна с загрузки системы).</summary>
    /// <exception cref="InvalidOperationException">Система не вернула частоту счётчика.</exception>
    public QpcMonotonicClock()
    {
        if (!QueryPerformanceFrequency(out long frequency) || frequency <= 0)
        {
            throw new InvalidOperationException("QueryPerformanceFrequency failed.");
        }

        Frequency = frequency;
    }

    /// <inheritdoc />
    public long Frequency { get; }

    /// <inheritdoc />
    public long Ticks
    {
        get
        {
            if (!QueryPerformanceCounter(out long ticks))
            {
                throw new InvalidOperationException("QueryPerformanceCounter failed.");
            }

            return ticks;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryPerformanceCounter")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryPerformanceCounter(out long value);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryPerformanceFrequency")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryPerformanceFrequency(out long value);
}
