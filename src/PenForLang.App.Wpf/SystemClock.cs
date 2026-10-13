using PenForLang.Core;

namespace PenForLang.App.Wpf;

/// <summary>
/// Реализация <see cref="IClock"/> на системных часах. Живёт в корне композиции, а не в домене (R2).
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
