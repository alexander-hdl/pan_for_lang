using System.Collections.Immutable;

namespace PenForLang.Ink;

/// <summary>
/// Собирает штрихи из потока точек: <see cref="PenPhase.Down"/> открывает штрих указателя,
/// <see cref="PenPhase.Move"/> дополняет, <see cref="PenPhase.Up"/> закрывает. Точки hover и точки контакта без
/// предшествующего Down (указатель не принадлежит холсту) пропускаются. Штрихи нумеруются по порядку начала.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Точка, время которой меньше времени предыдущей принятой точки того же указателя, отбрасывается и учитывается в
/// <see cref="DroppedNonMonotonicCount"/>: сборка не бросает исключений из-за «дрожащих» меток времени.</item>
/// <item>Нажатие или отпускание барабанной кнопки/ластика посреди контакта делит штрих: новый штрих начинается с точки,
/// в которой изменилось состояние (<see cref="PenSample.IsAuxiliary"/>), без фазы Down. Так вспомогательные точки
/// оказываются в отдельных штрихах, которые проекция в линейку отбрасывает целиком (а не «склеивает» рывком края штриха).</item>
/// <item>Все точки попытки лежат в одном растущем буфере; штрихи материализуются в <see cref="ToStrokes"/>.</item>
/// </list>
/// </remarks>
public sealed class StrokeAssembler
{
    private readonly List<PenSample> _samples = [];
    private readonly List<int> _strokeOfSample = [];
    private readonly List<long> _lastTimeOfStroke = [];
    private readonly List<bool> _auxiliaryOfStroke = [];
    private readonly Dictionary<uint, int> _openStrokeOfPointer = [];

    /// <summary>Число штрихов (открытых и закрытых) на данный момент.</summary>
    public int StrokeCount => _lastTimeOfStroke.Count;

    /// <summary>Число штрихов, ожидающих <see cref="PenPhase.Up"/>.</summary>
    public int OpenStrokeCount => _openStrokeOfPointer.Count;

    /// <summary>Сколько точек отброшено из-за убывающего времени.</summary>
    public int DroppedNonMonotonicCount { get; private set; }

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
                _openStrokeOfPointer.Remove(sample.PointerId);
                StartStroke(sample);
                break;

            case PenPhase.Move:
            case PenPhase.Up:
                AddContact(sample);
                break;

            default:
                break;
        }
    }

    /// <summary>Закрывает указатель без <see cref="PenPhase.Up"/> (потеря захвата): штрих остаётся как есть.</summary>
    public void Release(uint pointerId) => _openStrokeOfPointer.Remove(pointerId);

    /// <summary>Возвращает собранные штрихи (открытые тоже). Сборщик остаётся пригодным для дальнейшей работы; метод не бросает.</summary>
    public ImmutableArray<InkStroke> ToStrokes()
    {
        int strokeCount = _lastTimeOfStroke.Count;
        var counts = new int[strokeCount];
        foreach (int stroke in _strokeOfSample)
        {
            counts[stroke]++;
        }

        var builders = new ImmutableArray<PenSample>.Builder[strokeCount];
        for (int i = 0; i < strokeCount; i++)
        {
            builders[i] = ImmutableArray.CreateBuilder<PenSample>(counts[i]);
        }

        for (int i = 0; i < _samples.Count; i++)
        {
            builders[_strokeOfSample[i]].Add(_samples[i]);
        }

        var result = ImmutableArray.CreateBuilder<InkStroke>(strokeCount);
        for (int i = 0; i < strokeCount; i++)
        {
            result.Add(new InkStroke(i, builders[i].MoveToImmutable()));
        }

        return result.ToImmutable();
    }

    private void AddContact(in PenSample sample)
    {
        if (!_openStrokeOfPointer.TryGetValue(sample.PointerId, out int stroke))
        {
            return;
        }

        if (sample.TimeUs < _lastTimeOfStroke[stroke])
        {
            DroppedNonMonotonicCount++;
            return;
        }

        if (sample.IsAuxiliary != _auxiliaryOfStroke[stroke])
        {
            stroke = StartStroke(sample);
        }
        else
        {
            Append(stroke, sample);
        }

        if (sample.Phase == PenPhase.Up)
        {
            _openStrokeOfPointer.Remove(sample.PointerId);
        }
    }

    private int StartStroke(in PenSample sample)
    {
        int stroke = _lastTimeOfStroke.Count;
        _lastTimeOfStroke.Add(sample.TimeUs);
        _auxiliaryOfStroke.Add(sample.IsAuxiliary);
        _openStrokeOfPointer[sample.PointerId] = stroke;
        _samples.Add(sample);
        _strokeOfSample.Add(stroke);
        return stroke;
    }

    private void Append(int stroke, in PenSample sample)
    {
        _lastTimeOfStroke[stroke] = sample.TimeUs;
        _samples.Add(sample);
        _strokeOfSample.Add(stroke);
    }
}
