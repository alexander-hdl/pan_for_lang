using System.Collections.Immutable;

namespace PenForLang.Ink;

/// <summary>
/// Штрих: непрерывная последовательность точек от касания до отрыва пера (ARCHITECTURE.md §3.7).
/// Точки упорядочены по времени неубывающе. Экземпляр неизменяем.
/// </summary>
/// <remarks>
/// Ловушка равенства: у записей с <see cref="ImmutableArray{T}"/> равенство по значению сравнивает ссылку на внутренний массив,
/// а не элементы. Два штриха с одинаковыми точками, собранными независимо, не равны по <c>==</c>/<c>Equals</c>;
/// сравнивайте содержимое через <c>Samples.SequenceEqual(...)</c>. То же относится к <see cref="InkAttempt"/> и <see cref="RulerInk"/>.
/// </remarks>
public sealed record InkStroke
{
    /// <summary>Создаёт штрих.</summary>
    /// <param name="index">Порядковый номер штриха в попытке (с 0).</param>
    /// <param name="samples">Точки; пустой массив допустим.</param>
    /// <exception cref="ArgumentOutOfRangeException">Индекс отрицателен.</exception>
    /// <exception cref="ArgumentException">Точки не упорядочены по времени.</exception>
    public InkStroke(int index, ImmutableArray<PenSample> samples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Index = index;
        Samples = samples.IsDefault ? [] : samples;
        for (int i = 1; i < Samples.Length; i++)
        {
            if (Samples[i].TimeUs < Samples[i - 1].TimeUs)
            {
                throw new ArgumentException("Samples must be ordered by time.", nameof(samples));
            }
        }
    }

    /// <summary>Порядковый номер штриха в попытке (с 0).</summary>
    public int Index { get; }

    /// <summary>Точки штриха.</summary>
    public ImmutableArray<PenSample> Samples { get; }

    /// <summary>Длительность штриха в микросекундах (0 для пустого или одноточечного штриха).</summary>
    public long DurationUs => Samples.Length < 2 ? 0 : Samples[^1].TimeUs - Samples[0].TimeUs;
}
