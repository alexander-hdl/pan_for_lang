using System.Runtime.InteropServices;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Обращения к ОС, нужные обработчику сообщений <c>WM_POINTER</c>. Вынесены в интерфейс, чтобы весь путь
/// «сообщение → точки» тестировался без окна на фейковых <c>POINTER_PEN_INFO</c>.
/// </summary>
internal interface IPointerApi
{
    /// <summary>Тип указателя (<c>POINTER_INPUT_TYPE</c>); <c>false</c>, если ОС не знает такого указателя.</summary>
    bool TryGetPointerType(uint pointerId, out uint pointerType);

    /// <summary>
    /// <c>GetPointerPenInfoHistory</c>: заполняет <paramref name="buffer"/> записями в ОБРАТНОМ хронологическом порядке
    /// (новейшая — первая). <paramref name="total"/> на входе — размер буфера, на выходе — число доступных записей; если оно больше
    /// размера буфера, в буфере лежат только новейшие записи.
    /// </summary>
    /// <returns><c>false</c>, если ОС отказала (<c>ERROR_NO_DATA</c>, <c>ERROR_DATATYPE_MISMATCH</c>, ...).</returns>
    bool TryGetPenInfoHistory(uint pointerId, Span<NativePointerPenInfo> buffer, ref uint total);

    /// <summary><c>GetPointerPenInfo</c>: одиночная запись текущего сообщения (запасной путь при сбое истории, F5).</summary>
    bool TryGetPenInfo(uint pointerId, out NativePointerPenInfo info);

    /// <summary>Код Win32 последнего неудачного вызова этого API (0, если сбоев не было).</summary>
    int LastError { get; }
}

/// <summary>Реальная реализация <see cref="IPointerApi"/> на user32.</summary>
internal sealed class Win32PointerApi : IPointerApi
{
    /// <inheritdoc />
    public int LastError { get; private set; }

    /// <inheritdoc />
    public bool TryGetPenInfo(uint pointerId, out NativePointerPenInfo info)
    {
        if (NativeMethods.GetPointerPenInfo(pointerId, out info))
        {
            return true;
        }

        LastError = Marshal.GetLastPInvokeError();
        return false;
    }

    /// <inheritdoc />
    public bool TryGetPointerType(uint pointerId, out uint pointerType) =>
        NativeMethods.GetPointerType(pointerId, out pointerType);

    /// <inheritdoc />
    public bool TryGetPenInfoHistory(uint pointerId, Span<NativePointerPenInfo> buffer, ref uint total)
    {
        if (buffer.IsEmpty)
        {
            return NativeMethods.GetPointerPenInfoHistoryCount(pointerId, ref total, 0);
        }

        total = (uint)buffer.Length;
        if (NativeMethods.GetPointerPenInfoHistory(pointerId, ref total, ref MemoryMarshal.GetReference(buffer)))
        {
            return true;
        }

        LastError = Marshal.GetLastPInvokeError();
        return false;
    }
}
