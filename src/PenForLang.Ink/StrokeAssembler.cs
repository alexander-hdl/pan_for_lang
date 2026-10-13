using System.Collections.Immutable;

namespace PenForLang.Ink;

/// <summary>
/// Собирает штрихи из потока точек: <see cref="PenPhase.Down"/> открывает штрих указателя,
/// <see cref="PenPhase.Move"/> дополняет, <see cref="PenPhase.Up"/> закрывает. Точки hover и точки контакта без
/// предшествующего Down (указатель не принадлежит холсту) пропускаются. Штрихи нумеруются по порядку касаний.
/// </summary>
public sealed class StrokeAssembler
{
    private readonly List<List<PenSample>> _strokes = [];
    private readonly Dictionary<uint, List<PenSample>> _open = [];

    /// <summary>Число штрихов (открытых и закрытых) на данный момент.</summary>
    public int StrokeCount => _strokes.Count;

    /// <summary>Число штрихов, ожидающих <see cref="PenPhase.Up"/>.</summary>
    public int OpenStrokeCount => _open.Count;

    /// <summary>Добавляет пачку точек в хронологическом порядке.</summary>
    public void Add(ReadOnlySpan<PenSample> samples)
    {
        foreach (PenSample sample in samples)
        {
            Add(sample);
        }
    }

    /// <summary>Добавляет одну точку.</summary>
    public void Add(in PenSample sample)
    {
        switch (sample.Phase)
        {
            case PenPhase.Down:
                // Повторный Down без Up: прежний штрих считается оборванным.
                _open.Remove(sample.PointerId);
                var stroke = new List<PenSample> { sample };
                _strokes.Add(stroke);
                _open[sample.PointerId] = stroke;
                break;
            case PenPhase.Move:
                if (_open.TryGetValue(sample.PointerId, out List<PenSample>? moving))
                {
                    moving.Add(sample);
                }

                break;
            case PenPhase.Up:
                if (_open.Remove(sample.PointerId, out List<PenSample>? closing))
                {
                    closing.Add(sample);
                }

                break;
            default:
                break;
        }
    }

    /// <summary>Закрывает указатель без <see cref="PenPhase.Up"/> (потеря захвата): штрих остаётся как есть.</summary>
    public void Release(uint pointerId) => _open.Remove(pointerId);

    /// <summary>Возвращает собранные штрихи (открытые тоже). Сборщик остаётся пригодным для дальнейшей работы.</summary>
    /// <exception cref="ArgumentException">Точки штриха не упорядочены по времени.</exception>
    public ImmutableArray<InkStroke> ToStrokes()
    {
        var result = ImmutableArray.CreateBuilder<InkStroke>(_strokes.Count);
        for (int i = 0; i < _strokes.Count; i++)
        {
            result.Add(new InkStroke(i, [.. _strokes[i]]));
        }

        return result.ToImmutable();
    }
}
