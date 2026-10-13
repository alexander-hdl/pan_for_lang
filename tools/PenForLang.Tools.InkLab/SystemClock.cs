using PenForLang.Core;

namespace PenForLang.Tools.InkLab;

/// <summary>Календарные часы системы (корень композиции инструмента; домену читать системное время запрещено — R2).</summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
