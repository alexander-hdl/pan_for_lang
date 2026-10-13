namespace PenForLang.Ink;

/// <summary>Фаза указателя в точке пера.</summary>
public enum PenPhase : byte
{
    /// <summary>Перо над поверхностью (контакта нет).</summary>
    Hover,

    /// <summary>Касание: первая точка штриха.</summary>
    Down,

    /// <summary>Перо движется в контакте с поверхностью.</summary>
    Move,

    /// <summary>Отрыв: последняя точка штриха.</summary>
    Up,
}

/// <summary>Маска кнопок пера в точке. Значения совпадают с полем <c>btn</c> формата penlog.</summary>
[Flags]
public enum PenButtons : byte
{
    /// <summary>Кнопок нет.</summary>
    None = 0,

    /// <summary>Барабанная кнопка.</summary>
    Barrel = 1,

    /// <summary>Перо перевёрнуто (ластик).</summary>
    Eraser = 2,
}

/// <summary>Служебные признаки точки.</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name is fixed by the contract in ARCHITECTURE.md section 3.7.")]
public enum SampleFlags : byte
{
    /// <summary>Нет признаков.</summary>
    None = 0,

    /// <summary>Время точки интерполировано внутри пачки (источник не даёт собственных меток), <c>ti</c> в penlog.</summary>
    InterpolatedTime = 1,

    /// <summary>Точка получена из истории/слияния пачки (coalesced), <c>co</c> в penlog.</summary>
    Coalesced = 2,

    /// <summary>Точка за краем холста (A11), <c>clip</c> в penlog.</summary>
    Clipped = 4,
}

/// <summary>
/// Одна точка пера в системе координат поверхности письма (ARCHITECTURE.md §3.1, §3.7).
/// </summary>
/// <param name="TimeUs">Время в микросекундах от начала задания (монотонные часы; для QPC — тики, переведённые в мкс).</param>
/// <param name="X">Физические пиксели от левого верхнего угла холста (DPI не применяется, A12).</param>
/// <param name="Y">Физические пиксели от левого верхнего угла холста; ось вниз.</param>
/// <param name="Pressure">Давление, номинально 0..1 (0 — нет данных/нет контакта).</param>
/// <param name="TiltXDeg">Наклон по X в градусах (0, если устройство наклон не сообщает).</param>
/// <param name="TiltYDeg">Наклон по Y в градусах (0, если устройство наклон не сообщает).</param>
/// <param name="Phase">Фаза указателя.</param>
/// <param name="Buttons">Нажатые кнопки пера.</param>
/// <param name="Flags">Служебные признаки.</param>
/// <param name="PointerId">Идентификатор указателя (<c>pointerId</c> Windows) — по нему определяется владение (A11).</param>
public readonly record struct PenSample(
    long TimeUs,
    double X,
    double Y,
    float Pressure,
    float TiltXDeg,
    float TiltYDeg,
    PenPhase Phase,
    PenButtons Buttons,
    SampleFlags Flags,
    uint PointerId)
{
    /// <summary>Точка принадлежит контакту (не <see cref="PenPhase.Hover"/>).</summary>
    public bool IsContact => Phase != PenPhase.Hover;

    /// <summary>Точка помечена как выходящая за край холста.</summary>
    public bool IsClipped => (Flags & SampleFlags.Clipped) != 0;

    /// <summary>Нажата барабанная кнопка или перо перевёрнуто — такие точки не идут в оценку.</summary>
    public bool IsAuxiliary => (Buttons & (PenButtons.Barrel | PenButtons.Eraser)) != 0;
}
