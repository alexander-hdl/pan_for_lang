namespace PenForLang.Core;

/// <summary>
/// Детерминированный генератор PCG-XSH-RR 64/32 (pcg-random.org, эталонная реализация <c>pcg32_random_r</c>).
/// Одинаковые <c>seed</c> и <c>sequence</c> дают одинаковую последовательность на любой машине.
/// </summary>
public sealed class Pcg32 : IRandomSource
{
    private const ulong Multiplier = 6364136223846793005UL;

    private readonly ulong _increment;
    private ulong _state;

    /// <param name="seed">Начальное состояние.</param>
    /// <param name="sequence">Номер потока (разные значения дают независимые последовательности).</param>
    public Pcg32(ulong seed, ulong sequence = 54UL)
    {
        _state = 0UL;
        _increment = (sequence << 1) | 1UL;
        NextUInt32();
        _state += seed;
        NextUInt32();
    }

    /// <inheritdoc />
    public uint NextUInt32()
    {
        ulong old = _state;
        _state = unchecked((old * Multiplier) + _increment);
        uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
    }

    /// <inheritdoc />
    public int NextInt32(int exclusiveUpperBound)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveUpperBound);

        // Отбрасывание «хвоста» (как pcg32_boundedrand_r): без смещения по модулю.
        uint bound = (uint)exclusiveUpperBound;
        uint threshold = (0u - bound) % bound;
        while (true)
        {
            uint r = NextUInt32();
            if (r >= threshold)
            {
                return (int)(r % bound);
            }
        }
    }

    /// <inheritdoc />
    public double NextDouble() => NextUInt32() / 4294967296.0;
}
