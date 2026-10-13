namespace PenForLang.Ink;

/// <summary>
/// Штрих: непрерывная последовательность точек от касания до отрыва пера.
/// Точки упорядочены по времени неубывающе.
/// </summary>
public sealed class InkStroke
{
    private readonly PenSample[] _samples;

    /// <exception cref="ArgumentException">Точки не упорядочены по времени.</exception>
    public InkStroke(IEnumerable<PenSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _samples = samples.ToArray();
        for (int i = 1; i < _samples.Length; i++)
        {
            if (_samples[i].TimeMicros < _samples[i - 1].TimeMicros)
            {
                throw new ArgumentException("Samples must be ordered by time.", nameof(samples));
            }
        }
    }

    /// <summary>Точки штриха.</summary>
    public IReadOnlyList<PenSample> Samples => _samples;

    /// <summary>Длительность штриха в микросекундах (0 для пустого или одноточечного штриха).</summary>
    public long DurationMicros => _samples.Length < 2 ? 0 : _samples[^1].TimeMicros - _samples[0].TimeMicros;
}
