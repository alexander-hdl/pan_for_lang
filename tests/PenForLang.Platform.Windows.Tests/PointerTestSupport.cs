using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows.Tests;

/// <summary>Фейковый API указателей: история задаётся списком «новейшая первой», как отдаёт <c>GetPointerPenInfoHistory</c>.</summary>
internal sealed class FakePointerApi : IPointerApi
{
    /// <summary>Тип указателя по умолчанию — перо.</summary>
    public uint DefaultType { get; set; } = NativeConstants.PtPen;

    public Dictionary<uint, uint> Types { get; } = [];

    /// <summary>Записи истории в порядке ОС: новейшая — первая.</summary>
    public List<NativePointerPenInfo> NewestFirst { get; } = [];

    public bool FailHistory { get; set; }

    public int HistoryCalls { get; private set; }

    public bool TryGetPointerType(uint pointerId, out uint pointerType)
    {
        pointerType = Types.TryGetValue(pointerId, out uint t) ? t : DefaultType;
        return true;
    }

    public bool TryGetPenInfoHistory(uint pointerId, Span<NativePointerPenInfo> buffer, ref uint total)
    {
        HistoryCalls++;
        if (FailHistory)
        {
            return false;
        }

        // Семантика ОС: при нехватке места в буфере лежат новейшие записи, total — сколько всего доступно.
        int n = Math.Min(NewestFirst.Count, buffer.Length);
        for (int i = 0; i < n; i++)
        {
            buffer[i] = NewestFirst[i];
        }

        total = (uint)NewestFirst.Count;
        return true;
    }

    /// <summary>Задаёт историю одним сообщением по хронологическому списку (внутри разворачивается в порядок ОС).</summary>
    public void SetHistoryChronological(params NativePointerPenInfo[] chronological)
    {
        NewestFirst.Clear();
        for (int i = chronological.Length - 1; i >= 0; i--)
        {
            NewestFirst.Add(chronological[i]);
        }
    }
}

/// <summary>Конструктор фейковых <c>POINTER_PEN_INFO</c>.</summary>
internal static class Pen
{
    public const uint Contact = NativeConstants.PointerFlagInRange | NativeConstants.PointerFlagInContact | 0x10;
    public const uint FullMask = NativeConstants.PenMaskPressure | NativeConstants.PenMaskTiltX | NativeConstants.PenMaskTiltY;

    public static NativePointerPenInfo Down(uint id, int x, int y, ulong perf = 1_000_000, uint pressure = 512) =>
        Make(id, Contact | NativeConstants.PointerFlagDown, x, y, perf, pressure);

    public static NativePointerPenInfo Move(uint id, int x, int y, ulong perf, uint pressure = 512) =>
        Make(id, Contact | NativeConstants.PointerFlagUpdate, x, y, perf, pressure);

    public static NativePointerPenInfo Up(uint id, int x, int y, ulong perf) =>
        Make(id, NativeConstants.PointerFlagInRange | NativeConstants.PointerFlagUp, x, y, perf, 0);

    public static NativePointerPenInfo Hover(uint id, int x, int y, ulong perf) =>
        Make(id, NativeConstants.PointerFlagInRange | NativeConstants.PointerFlagUpdate, x, y, perf, 0);

    public static NativePointerPenInfo Make(uint id, uint pointerFlags, int x, int y, ulong perf, uint pressure) =>
        new()
        {
            PointerInfo = new NativePointerInfo
            {
                PointerType = NativeConstants.PtPen,
                PointerId = id,
                PointerFlags = pointerFlags,
                PtPixelLocation = new NativePoint { X = x, Y = y },
                PerformanceCount = perf,
            },
            PenMask = FullMask,
            Pressure = pressure,
        };
}
