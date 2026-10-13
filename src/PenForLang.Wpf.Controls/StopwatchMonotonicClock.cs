using System.Diagnostics;
using PenForLang.Core;

namespace PenForLang.Wpf.Controls;

/// <summary>
/// Монотонные часы на <see cref="Stopwatch"/> — запасной вариант, когда настоящие QPC-часы из <c>Platform.Windows</c> недоступны
/// (<c>Wpf.Controls</c> на неё не ссылается). <see cref="Stopwatch.GetTimestamp"/> в Windows — тот же QueryPerformanceCounter,
/// поэтому значения совместимы с <c>QpcMonotonicClock</c>.
/// </summary>
public sealed class StopwatchMonotonicClock : IMonotonicClock
{
    /// <inheritdoc />
    public long Frequency => Stopwatch.Frequency;

    /// <inheritdoc />
    public long Ticks => Stopwatch.GetTimestamp();
}
