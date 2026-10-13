namespace PenForLang.Ink;

/// <summary>
/// Чистая часть правила владения указателем по <c>pointerId</c> (ARCHITECTURE.md §7.1, A11).
/// Если <see cref="PenPhase.Down"/> пришёл внутри холста, все точки этого указателя до <see cref="PenPhase.Up"/>
/// (или <see cref="Release"/>) принадлежат холсту; точки за краем получают <see cref="SampleFlags.Clipped"/>.
/// Если Down пришёл вне холста, указатель холсту не принадлежит и его точки отбрасываются.
/// Hover внутри холста проходит как есть (для маркера кончика), вне холста отбрасывается.
/// Координаты точек — поверхности (физические px от угла холста).
/// </summary>
/// <remarks>
/// <para><b>Соответствие возвращаемого значения и «обработано» в <c>WM_POINTER</c>.</b> <see cref="TryProcess"/> вернул
/// <c>true</c> — сообщение принадлежит холсту: источник помечает его обработанным (не отдаёт дальше в WPF) и передаёт
/// <c>result</c> подписчикам. Вернул <c>false</c> — сообщение холсту не принадлежит: источник НЕ помечает его обработанным,
/// оно уходит в WPF (например, нажатия на кнопки интерфейса пером). Исключение — потеря захвата
/// (<c>WM_POINTERCAPTURECHANGED</c>): источник вызывает <see cref="Release"/> и сообщение обрабатывает как обычно.</para>
/// <para><b>Потерянный Up.</b> Если пришёл hover по указателю, который считался владеемым (Up потерян), это неявный отрыв:
/// владение снимается, а в <c>result</c> возвращается точка с фазой <see cref="PenPhase.Up"/> в позиции hover-точки,
/// чтобы штрих закрылся. Повторный Down по уже владеемому указателю владение сохраняет (сборщик штрихов начнёт новый штрих);
/// Down вне холста по владеемому указателю снимает владение и отбрасывается.</para>
/// <para>Размер холста меняется через <see cref="SetSize"/> (изменение размера окна, смена монитора), не создавая трекер заново.</para>
/// </remarks>
public sealed class PointerOwnership
{
    private readonly HashSet<uint> _owned = [];

    /// <summary>Создаёт трекер для холста заданного размера.</summary>
    /// <param name="widthPx">Ширина холста, физические px; &gt; 0.</param>
    /// <param name="heightPx">Высота холста, физические px; &gt; 0.</param>
    /// <exception cref="ArgumentException">Размер не конечен или не положителен.</exception>
    public PointerOwnership(double widthPx, double heightPx)
    {
        SetSize(widthPx, heightPx);
    }

    /// <summary>Ширина холста, px.</summary>
    public double WidthPx { get; private set; }

    /// <summary>Высота холста, px.</summary>
    public double HeightPx { get; private set; }

    /// <summary>Сколько указателей сейчас принадлежат холсту.</summary>
    public int OwnedCount => _owned.Count;

    /// <summary>Принадлежит ли указатель холсту.</summary>
    public bool IsOwned(uint pointerId) => _owned.Contains(pointerId);

    /// <summary>Задаёт новый размер холста; владение указателями сохраняется.</summary>
    /// <exception cref="ArgumentException">Размер не конечен или не положителен.</exception>
    public void SetSize(double widthPx, double heightPx)
    {
        if (!double.IsFinite(widthPx) || widthPx <= 0 || !double.IsFinite(heightPx) || heightPx <= 0)
        {
            throw new ArgumentException("Canvas size must be finite and positive.");
        }

        WidthPx = widthPx;
        HeightPx = heightPx;
    }

    /// <summary>Задаёт размер холста по прямоугольнику поверхности (<see cref="PenSurface.WidthPx"/>, <see cref="PenSurface.HeightPx"/>).</summary>
    /// <exception cref="ArgumentException">Размер не конечен или не положителен.</exception>
    public void SetSurface(PenSurface surface) => SetSize(surface.WidthPx, surface.HeightPx);

    /// <summary>Применяет правило к точке.</summary>
    /// <param name="sample">Входная точка.</param>
    /// <param name="result">Точка для передачи дальше (с возможным флагом <see cref="SampleFlags.Clipped"/>).</param>
    /// <returns><c>true</c>, если сообщение принадлежит холсту (помечается обработанным, <c>result</c> передаётся дальше); иначе <c>false</c>.</returns>
    public bool TryProcess(in PenSample sample, out PenSample result)
    {
        bool inside = sample.X >= 0 && sample.Y >= 0 && sample.X <= WidthPx && sample.Y <= HeightPx;
        result = sample;

        switch (sample.Phase)
        {
            case PenPhase.Hover:
                if (_owned.Remove(sample.PointerId))
                {
                    // Неявный Up: владеемый указатель вдруг в состоянии hover.
                    result = sample with { Phase = PenPhase.Up, Flags = inside ? sample.Flags : sample.Flags | SampleFlags.Clipped };
                    return true;
                }

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

    /// <summary>Снимает владение со всех указателей (потеря устройства, смена окна).</summary>
    public void Reset() => _owned.Clear();
}
