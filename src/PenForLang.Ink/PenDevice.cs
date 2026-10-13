namespace PenForLang.Ink;

/// <summary>Вид источника ввода (ARCHITECTURE.md §7.1).</summary>
public enum PenSourceKind
{
    /// <summary>Сообщения <c>WM_POINTER</c> (основной кандидат), <c>wm_pointer</c> в penlog.</summary>
    Win32Pointer,

    /// <summary>WPF-стилус (WISP), <c>wpf_stylus</c> в penlog.</summary>
    WpfStylus,

    /// <summary>Воспроизведение лога, <c>replay</c> в penlog.</summary>
    Replay,

    /// <summary>Мышь как перо (режим разработчика), <c>mouse</c> в penlog.</summary>
    Mouse,
}

/// <summary>Происхождение меток времени точек.</summary>
public enum PenTimestampKind
{
    /// <summary>Метки устройства (QPC), <c>qpc</c> в penlog.</summary>
    Qpc,

    /// <summary>Метка на пачку, внутри пачки интерполяция, <c>interpolated</c> в penlog.</summary>
    Interpolated,
}

/// <summary>Описание устройства ввода — поле <c>device</c> заголовка penlog (ARCHITECTURE.md §3.4).</summary>
/// <param name="Product">Название устройства.</param>
/// <param name="Source">Вид источника ввода.</param>
/// <param name="RateHzNominal">Паспортная частота опроса, Гц, если известна.</param>
/// <param name="RateHzMeasured">Измеренная частота, Гц, если измерялась.</param>
/// <param name="PressureLevels">Число уровней давления, если известно.</param>
/// <param name="Tilt">Устройство сообщает наклон.</param>
/// <param name="Timestamps">Происхождение меток времени.</param>
public sealed record PenDeviceInfo(
    string Product,
    PenSourceKind Source,
    double? RateHzNominal,
    double? RateHzMeasured,
    int? PressureLevels,
    bool Tilt,
    PenTimestampKind Timestamps);

/// <summary>Вид события устройства.</summary>
public enum PenDeviceEventKind
{
    /// <summary>Устройство подключено (или источник стартовал).</summary>
    Connected,

    /// <summary>Устройство потеряно (§4.6).</summary>
    Lost,

    /// <summary>Монитор пера или его DPI изменились — нужно пересчитать поверхность.</summary>
    MonitorChanged,
}

/// <summary>Событие устройства ввода.</summary>
/// <param name="Kind">Вид события.</param>
/// <param name="Device">Описание устройства, если известно.</param>
public sealed record PenDeviceEvent(PenDeviceEventKind Kind, PenDeviceInfo? Device = null);
