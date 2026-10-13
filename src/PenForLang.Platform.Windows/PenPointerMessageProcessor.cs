using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>Счётчики обработчика <c>WM_POINTER</c> для панели диагностики и протокола V0 на железе.</summary>
/// <param name="PointerMessages">Всего сообщений WM_POINTER*, дошедших до обработчика.</param>
/// <param name="PenMessages">Из них от пера (<c>PT_PEN</c>).</param>
/// <param name="NonPenMessages">От других указателей (мышь, касание) — отданы дальше в WPF.</param>
/// <param name="HandledMessages">Сообщения, принятые холстом (источник пометил их обработанными).</param>
/// <param name="HistoryFailures">Сбои <c>GetPointerPenInfoHistory</c>.</param>
/// <param name="HistoryBufferGrowths">Сколько раз пришлось увеличить буфер истории.</param>
/// <param name="Samples">Выдано точек.</param>
/// <param name="CoalescedSamples">Из них из истории (флаг <see cref="SampleFlags.Coalesced"/>).</param>
/// <param name="ClippedSamples">Точек с флагом <see cref="SampleFlags.Clipped"/>.</param>
/// <param name="TimeFallbackSamples">Точек без метки <c>PerformanceCount</c> (время распределено между последним выданным и «сейчас»).</param>
/// <param name="EnterLeaveMessages">Сообщения ENTER/LEAVE: только считаются, точек не дают (F1).</param>
/// <param name="DuplicateFrames">Записи, отброшенные как повтор кадра (тот же <c>frameId</c>, что у последней обработанной записи указателя).</param>
/// <param name="HistoryFallbacks">Сколько раз сбой истории у владеемого указателя был заменён одиночным <c>GetPointerPenInfo</c> (F5).</param>
/// <param name="LastHistoryError">Код Win32 последнего сбоя <c>GetPointerPenInfoHistory</c> (0 — сбоев не было).</param>
public readonly record struct PointerDiagnostics(
    long PointerMessages,
    long PenMessages,
    long NonPenMessages,
    long HandledMessages,
    long HistoryFailures,
    long HistoryBufferGrowths,
    long Samples,
    long CoalescedSamples,
    long ClippedSamples,
    long TimeFallbackSamples,
    long EnterLeaveMessages,
    long DuplicateFrames,
    long HistoryFallbacks,
    int LastHistoryError);

/// <summary>
/// Обработка сообщений <c>WM_POINTER</c> пера без окна: история → хронологический порядок → <see cref="PenSample"/> →
/// владение по <c>pointerId</c> (<see cref="PointerOwnership"/>, A11) → пачка (ARCHITECTURE.md §7.1).
/// </summary>
/// <remarks>
/// <para><b>Контракт «обработано».</b> Метод <see cref="TryProcess"/> возвращает <c>true</c>, если хотя бы одна точка сообщения
/// принята холстом (<see cref="PointerOwnership.TryProcess"/> вернул <c>true</c>): источник тогда помечает сообщение
/// обработанным и не отдаёт его дальше в WPF. Для не-перьевых указателей, указателей вне холста и пока поверхность не задана
/// результат <c>false</c>. <c>WM_POINTERCAPTURECHANGED</c> никогда не помечается обработанным, но владение снимается,
/// а открытый штрих закрывается синтетической точкой <see cref="PenPhase.Up"/>.</para>
/// <para><b>ENTER/LEAVE (F1).</b> <c>WM_POINTERENTER</c> несёт тот же кадр, что и <c>WM_POINTERDOWN</c> (тот же <c>frameId</c>,
/// флаги с <c>POINTER_FLAG_DOWN</c>), а <c>WM_POINTERLEAVE</c> повторяет последний UPDATE; точки из них дали бы двойной Down и
/// фантомные штрихи из одной точки. Поэтому эти сообщения только считаются (<see cref="PointerDiagnostics.EnterLeaveMessages"/>)
/// и не обрабатываются. Дополнительно запись с <c>frameId</c>, равным <c>frameId</c> последней обработанной записи того же
/// указателя, отбрасывается (<c>frameId</c> = 0 — «неизвестен», проверка не применяется).</para>
/// <para><b>История.</b> <c>GetPointerPenInfoHistory</c> отдаёт записи от новейшей к старейшей и теряет их после следующего
/// сообщения потока, поэтому вызывается прямо в хуке. Записи разворачиваются в хронологический порядок; все, кроме новейшей,
/// получают <see cref="SampleFlags.Coalesced"/>. Если записей больше размера буфера, буфер увеличивается и вызов повторяется
/// (ОС при нехватке места отдаёт только новейшие); в установившемся режиме аллокаций нет. Если история недоступна у владеемого
/// указателя (F5), читается одиночная запись <c>GetPointerPenInfo</c>, а сообщение всё равно считается принадлежащим холсту.</para>
/// <para><b>Время без метки (F4).</b> Если у записей нет <c>PerformanceCount</c>, их время распределяется равномерно между
/// последним выданным временем указателя и «сейчас» с монотонным <c>max</c>; помечается <see cref="SampleFlags.InterpolatedTime"/>.</para>
/// <para>Не потокобезопасен: вызывается из потока окна. Возвращаемая память живёт до следующего вызова.</para>
/// </remarks>
internal sealed class PenPointerMessageProcessor
{
    private const int InitialCapacity = 128;

    private readonly IPointerApi _api;
    private readonly IMonotonicClock _clock;
    private readonly PointerOwnership _ownership = new(1, 1);
    private readonly Dictionary<uint, PenSample> _lastOwned = [];
    private readonly Dictionary<uint, uint> _lastFrame = [];
    private readonly Dictionary<uint, long> _lastTimeUs = [];

    private NativePointerPenInfo[] _history = new NativePointerPenInfo[InitialCapacity];
    private PenSample[] _batch = new PenSample[InitialCapacity + 1];
    private PenSurface? _surface;
    private PointerDiagnostics _diagnostics;

    /// <summary>Создаёт обработчик.</summary>
    public PenPointerMessageProcessor(IPointerApi api, IMonotonicClock clock)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(clock);
        _api = api;
        _clock = clock;
    }

    /// <summary>Экранный прямоугольник холста задан.</summary>
    public bool HasSurface => _surface is not null;

    /// <summary>Устройство хотя бы раз сообщило наклон.</summary>
    public bool TiltReported { get; private set; }

    /// <summary>Устройство хотя бы раз сообщило давление.</summary>
    public bool PressureReported { get; private set; }

    /// <summary>Счётчики с начала работы.</summary>
    public PointerDiagnostics Diagnostics => _diagnostics;

    /// <summary>Сколько указателей сейчас принадлежат холсту.</summary>
    public int OwnedCount => _ownership.OwnedCount;

    /// <summary>Сообщение относится к обрабатываемым источником (<c>WM_POINTER*</c> из ARCHITECTURE.md §7.1).</summary>
    public static bool IsPointerMessage(uint message) =>
        message is NativeConstants.WmPointerDown
            or NativeConstants.WmPointerUpdate
            or NativeConstants.WmPointerUp
            or NativeConstants.WmPointerEnter
            or NativeConstants.WmPointerLeave
            or NativeConstants.WmPointerCaptureChanged;

    /// <summary><c>GET_POINTERID_WPARAM</c>: идентификатор указателя — младшее слово wParam.</summary>
    public static uint PointerIdOf(nint wParam) => (uint)(wParam & 0xFFFF);

    /// <summary>Задаёт экранный прямоугольник холста (физические px); владение указателями сохраняется.</summary>
    public void SetSurface(PenSurface surface)
    {
        _ownership.SetSurface(surface);
        _surface = surface;
    }

    /// <summary>Снимает владение со всех указателей (остановка источника, потеря устройства).</summary>
    public void Reset()
    {
        _ownership.Reset();
        _lastOwned.Clear();
        _lastFrame.Clear();
        _lastTimeUs.Clear();
    }

    /// <summary>
    /// Обрабатывает сообщение.
    /// </summary>
    /// <param name="message">Код сообщения.</param>
    /// <param name="wParam">wParam сообщения (в младшем слове — pointerId).</param>
    /// <param name="samples">Пачка точек в хронологическом порядке (может быть пустой); живёт до следующего вызова.</param>
    /// <returns><c>true</c> — сообщение принадлежит холсту и должно быть помечено обработанным.</returns>
    public bool TryProcess(uint message, nint wParam, out ReadOnlyMemory<PenSample> samples)
    {
        samples = ReadOnlyMemory<PenSample>.Empty;
        if (!IsPointerMessage(message))
        {
            return false;
        }

        _diagnostics = _diagnostics with { PointerMessages = _diagnostics.PointerMessages + 1 };
        uint pointerId = PointerIdOf(wParam);

        if (message == NativeConstants.WmPointerCaptureChanged)
        {
            return ProcessCaptureChanged(pointerId, out samples);
        }

        if (message is NativeConstants.WmPointerEnter or NativeConstants.WmPointerLeave)
        {
            // F1: ENTER повторяет кадр DOWN, LEAVE — последний UPDATE; точек они не дают.
            _diagnostics = _diagnostics with { EnterLeaveMessages = _diagnostics.EnterLeaveMessages + 1 };
            return false;
        }

        // Владеемый указатель — заведомо перо; для остальных проверяем тип (мышь и касание уходят в WPF).
        bool owned = _ownership.IsOwned(pointerId);
        if (!owned)
        {
            if (!_api.TryGetPointerType(pointerId, out uint type) || type != NativeConstants.PtPen)
            {
                _diagnostics = _diagnostics with { NonPenMessages = _diagnostics.NonPenMessages + 1 };
                return false;
            }
        }

        _diagnostics = _diagnostics with { PenMessages = _diagnostics.PenMessages + 1 };
        if (_surface is not PenSurface surface)
        {
            return false;
        }

        int count = ReadHistoryChronologically(pointerId, owned, out bool ownedFallback);
        if (count == 0)
        {
            // F5: у владеемого указателя сообщение всё равно наше (иначе WPF превратит его в мышиное и порвёт штрих).
            return ownedFallback;
        }

        long nowTicks = _clock.Ticks;
        long nowUs = MonotonicTime.ToMicroseconds(nowTicks, _clock.Frequency);
        bool haveLast = _lastTimeUs.TryGetValue(pointerId, out long lastUs);
        int accepted = 0;
        for (int i = 0; i < count; i++)
        {
            ref readonly NativePointerPenInfo info = ref _history[i];

            uint frame = info.PointerInfo.FrameId;
            if (frame != 0 && _lastFrame.TryGetValue(pointerId, out uint lastFrame) && lastFrame == frame)
            {
                _diagnostics = _diagnostics with { DuplicateFrames = _diagnostics.DuplicateFrames + 1 };
                continue;
            }

            _lastFrame[pointerId] = frame;

            // F4: равномерное распределение между последним выданным временем указателя и «сейчас», монотонно.
            long fallbackUs = haveLast
                ? Math.Max(lastUs, lastUs + (long)((double)(nowUs - lastUs) * (i + 1) / count))
                : nowUs;
            bool newest = i == count - 1;
            PenSample sample = PenSampleConverter.Convert(
                in info, surface.OriginXPx, surface.OriginYPx, _clock.Frequency, fallbackUs, coalesced: !newest);

            if (!_ownership.TryProcess(sample, out PenSample result))
            {
                if (!_ownership.IsOwned(sample.PointerId))
                {
                    _lastOwned.Remove(sample.PointerId);
                }

                continue;
            }

            TrackOwned(result);
            _lastTimeUs[pointerId] = result.TimeUs;
            if (result.Phase == PenPhase.Up)
            {
                _lastFrame.Remove(pointerId);
            }

            _batch[accepted++] = result;
            Count(result, in info);
        }

        if (accepted == 0)
        {
            return false;
        }

        samples = _batch.AsMemory(0, accepted);
        _diagnostics = _diagnostics with { HandledMessages = _diagnostics.HandledMessages + 1 };
        return true;
    }

    private bool ProcessCaptureChanged(uint pointerId, out ReadOnlyMemory<PenSample> samples)
    {
        samples = ReadOnlyMemory<PenSample>.Empty;
        bool wasOwned = _ownership.IsOwned(pointerId);
        _ownership.Release(pointerId);
        _lastFrame.Remove(pointerId);
        _lastTimeUs.Remove(pointerId);

        // Потеря захвата посреди штриха: закрываем его точкой Up, иначе сборщик штрихов оставит штрих открытым.
        if (wasOwned && _lastOwned.Remove(pointerId, out PenSample last))
        {
            long nowUs = MonotonicTime.ToMicroseconds(_clock.Ticks, _clock.Frequency);
            _batch[0] = last with
            {
                Phase = PenPhase.Up,
                Pressure = 0f,
                TimeUs = Math.Max(nowUs, last.TimeUs),
                Flags = (last.Flags & ~SampleFlags.Coalesced) | SampleFlags.InterpolatedTime,
            };
            samples = _batch.AsMemory(0, 1);
        }

        // WM_POINTERCAPTURECHANGED обрабатывается как обычно (PointerOwnership: ARCHITECTURE.md §7.1) — не «обработано».
        return false;
    }

    /// <summary>
    /// Читает историю и разворачивает её в хронологический порядок; возвращает число записей в <c>_history</c>.
    /// При сбое у владеемого указателя читает одиночную запись (F5).
    /// </summary>
    private int ReadHistoryChronologically(uint pointerId, bool owned, out bool ownedFallback)
    {
        ownedFallback = false;
        uint total = (uint)_history.Length;
        if (!_api.TryGetPenInfoHistory(pointerId, _history, ref total))
        {
            return HandleHistoryFailure(pointerId, owned, out ownedFallback);
        }

        if (total > _history.Length)
        {
            // ОС вернула только новейшие записи; расширяем буфер и читаем заново (сообщение ещё текущее).
            int capacity = (int)Math.Min(total, 1 << 16) + 16;
            _history = new NativePointerPenInfo[capacity];
            _batch = new PenSample[capacity + 1];
            _diagnostics = _diagnostics with { HistoryBufferGrowths = _diagnostics.HistoryBufferGrowths + 1 };
            total = (uint)_history.Length;
            if (!_api.TryGetPenInfoHistory(pointerId, _history, ref total))
            {
                return HandleHistoryFailure(pointerId, owned, out ownedFallback);
            }
        }

        int count = (int)Math.Min(total, (uint)_history.Length);
        _history.AsSpan(0, count).Reverse();
        return count;
    }

    private int HandleHistoryFailure(uint pointerId, bool owned, out bool ownedFallback)
    {
        _diagnostics = _diagnostics with
        {
            HistoryFailures = _diagnostics.HistoryFailures + 1,
            LastHistoryError = _api.LastError,
        };

        ownedFallback = owned;
        if (owned && _api.TryGetPenInfo(pointerId, out NativePointerPenInfo single))
        {
            _history[0] = single;
            _diagnostics = _diagnostics with { HistoryFallbacks = _diagnostics.HistoryFallbacks + 1 };
            return 1;
        }

        return 0;
    }

    private void TrackOwned(in PenSample result)
    {
        if (result.Phase == PenPhase.Up)
        {
            _lastOwned.Remove(result.PointerId);
        }
        else if (_ownership.IsOwned(result.PointerId))
        {
            _lastOwned[result.PointerId] = result;
        }
    }

    private void Count(in PenSample result, in NativePointerPenInfo info)
    {
        if ((info.PenMask & NativeConstants.PenMaskPressure) != 0)
        {
            PressureReported = true;
        }

        if ((info.PenMask & (NativeConstants.PenMaskTiltX | NativeConstants.PenMaskTiltY)) != 0)
        {
            TiltReported = true;
        }

        _diagnostics = _diagnostics with
        {
            Samples = _diagnostics.Samples + 1,
            CoalescedSamples = _diagnostics.CoalescedSamples + ((result.Flags & SampleFlags.Coalesced) != 0 ? 1 : 0),
            ClippedSamples = _diagnostics.ClippedSamples + (result.IsClipped ? 1 : 0),
            TimeFallbackSamples = _diagnostics.TimeFallbackSamples + ((result.Flags & SampleFlags.InterpolatedTime) != 0 ? 1 : 0),
        };
    }
}
