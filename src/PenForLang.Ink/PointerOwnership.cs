namespace PenForLang.Ink;

/// <summary>
/// Чистая часть правила владения указателем по <c>pointerId</c> (ARCHITECTURE.md §7.1, A11).
/// Если <see cref="PenPhase.Down"/> пришёл внутри холста, все точки этого указателя до <see cref="PenPhase.Up"/>
/// (или <see cref="Release"/>) принадлежат холсту; точки за краем получают <see cref="SampleFlags.Clipped"/>.
/// Если Down пришёл вне холста, указатель холсту не принадлежит и его точки отбрасываются.
/// Hover внутри холста проходит как есть (для маркера кончика), вне холста отбрасывается.
/// Координаты точек — поверхности (физические px от угла холста).
/// </summary>
public sealed class PointerOwnership
{
    private readonly HashSet<uint> _owned = [];

    /// <summary>Создаёт трекер для холста заданного размера.</summary>
    /// <param name="widthPx">Ширина холста, физические px; &gt; 0.</param>
    /// <param name="heightPx">Высота холста, физические px; &gt; 0.</param>
    /// <exception cref="ArgumentException">Размер не конечен или не положителен.</exception>
    public PointerOwnership(double widthPx, double heightPx)
    {
        if (!double.IsFinite(widthPx) || widthPx <= 0 || !double.IsFinite(heightPx) || heightPx <= 0)
        {
            throw new ArgumentException("Canvas size must be finite and positive.");
        }

        WidthPx = widthPx;
        HeightPx = heightPx;
    }

    /// <summary>Ширина холста, px.</summary>
    public double WidthPx { get; }

    /// <summary>Высота холста, px.</summary>
    public double HeightPx { get; }

    /// <summary>Сколько указателей сейчас принадлежат холсту.</summary>
    public int OwnedCount => _owned.Count;

    /// <summary>Принадлежит ли указатель холсту.</summary>
    public bool IsOwned(uint pointerId) => _owned.Contains(pointerId);

    /// <summary>
    /// Применяет правило к точке.
    /// </summary>
    /// <param name="sample">Входная точка.</param>
    /// <param name="result">Точка для передачи дальше (с возможным флагом <see cref="SampleFlags.Clipped"/>).</param>
    /// <returns><c>true</c>, если точку нужно обработать; <c>false</c> — она не принадлежит холсту.</returns>
    public bool TryProcess(in PenSample sample, out PenSample result)
    {
        bool inside = sample.X >= 0 && sample.Y >= 0 && sample.X <= WidthPx && sample.Y <= HeightPx;
        result = sample;

        switch (sample.Phase)
        {
            case PenPhase.Hover:
                return inside;

            case PenPhase.Down:
                if (!inside)
                {
                    _owned.Remove(sample.PointerId);
                    return false;
                }

                _owned.Add(sample.PointerId);
                return true;

            case PenPhase.Move:
                if (!_owned.Contains(sample.PointerId))
                {
                    return false;
                }

                break;

            case PenPhase.Up:
                if (!_owned.Remove(sample.PointerId))
                {
                    return false;
                }

                break;

            default:
                return false;
        }

        if (!inside)
        {
            result = sample with { Flags = sample.Flags | SampleFlags.Clipped };
        }

        return true;
    }

    /// <summary>Потеря захвата указателя (<c>WM_POINTERCAPTURECHANGED</c>): владение снимается.</summary>
    public void Release(uint pointerId) => _owned.Remove(pointerId);

    /// <summary>Снимает владение со всех указателей.</summary>
    public void Reset() => _owned.Clear();
}
