namespace PenForLang.Core;

/// <summary>
/// Порт случайности. Домен не использует <c>System.Random</c> (R2): источник детерминирован и
/// воспроизводим по зерну.
/// </summary>
public interface IRandomSource
{
    /// <summary>Следующее равномерное 32-битное число.</summary>
    uint NextUInt32();

    /// <summary>Равномерное целое в диапазоне [0, <paramref name="exclusiveUpperBound"/>) без смещения.</summary>
    int NextInt32(int exclusiveUpperBound);

    /// <summary>Равномерное число с плавающей точкой в диапазоне [0, 1).</summary>
    double NextDouble();
}
