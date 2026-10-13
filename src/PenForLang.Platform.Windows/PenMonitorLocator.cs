using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows;

/// <summary>
/// Опрос ОС для выбора монитора пера: <c>GetPointerDevices</c>, <c>EnumDisplayMonitors</c>, <c>GetMonitorInfo</c>,
/// <c>GetDpiForMonitor</c>, <c>GetDeviceCaps(HORZSIZE/VERTSIZE)</c> (ARCHITECTURE.md §7.4). Сам выбор — чистая функция
/// <see cref="PenMonitorSelector"/>. Поведение на XPPen — [не проверено, V0.5/S1].
/// </summary>
public static class PenMonitorLocator
{
    /// <summary>Опрашивает систему и выбирает монитор пера.</summary>
    /// <param name="forcedMonitorIndex">Принудительный монитор: номер (с 1) в порядке <see cref="EnumerateMonitors"/>; <c>null</c> — автоматический выбор.</param>
    public static PenMonitorChoice Locate(int? forcedMonitorIndex = null)
    {
        IReadOnlyList<MonitorDescription> monitors = EnumerateMonitors();
        return forcedMonitorIndex is int index
            ? PenMonitorSelector.SelectForced(monitors, index, EnumeratePointerDevices())
            : PenMonitorSelector.Select(EnumeratePointerDevices(), monitors);
    }

    /// <summary>Все устройства-указатели (<c>GetPointerDevices</c>); пустой список, если API недоступен или устройств нет.</summary>
    public static IReadOnlyList<PointerDeviceDescription> EnumeratePointerDevices()
    {
        try
        {
            uint count = 0;
            if (!NativeMethods.GetPointerDevicesCount(ref count, 0) || count == 0)
            {
                return [];
            }

            var buffer = new NativePointerDeviceInfo[count];
            if (!NativeMethods.GetPointerDevices(ref count, ref MemoryMarshal.GetArrayDataReference(buffer)))
            {
                return [];
            }

            var result = new List<PointerDeviceDescription>((int)count);
            for (int i = 0; i < Math.Min(count, (uint)buffer.Length); i++)
            {
                result.Add(new PointerDeviceDescription(
                    buffer[i].Device,
                    buffer[i].PointerDeviceType switch
                    {
                        NativeConstants.PointerDeviceTypeIntegratedPen => PointerDeviceKind.IntegratedPen,
                        NativeConstants.PointerDeviceTypeExternalPen => PointerDeviceKind.ExternalPen,
                        NativeConstants.PointerDeviceTypeTouch => PointerDeviceKind.Touch,
                        NativeConstants.PointerDeviceTypeTouchPad => PointerDeviceKind.TouchPad,
                        _ => PointerDeviceKind.Other,
                    },
                    buffer[i].Monitor,
                    buffer[i].GetProductString()));
            }

            return result;
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>Все мониторы с границами, DPI и физическим размером (если ОС его сообщает).</summary>
    public static unsafe IReadOnlyList<MonitorDescription> EnumerateMonitors()
    {
        var monitors = new List<MonitorDescription>();
        GCHandle handle = GCHandle.Alloc(monitors);
        try
        {
            NativeMethods.EnumDisplayMonitors(&OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    // Исключение, вышедшее из UnmanagedCallersOnly, роняет процесс (F6): любой сбой — остановка перечисления.
    [UnmanagedCallersOnly]
    private static unsafe int OnMonitor(nint monitor, nint hdc, NativeRect* rect, nint data)
    {
        try
        {
            return OnMonitorCore(monitor, data);
        }
#pragma warning disable CA1031 // Callback из нативного кода не вправе выпускать исключения.
        catch (Exception)
#pragma warning restore CA1031
        {
            return 0;
        }
    }

    private static unsafe int OnMonitorCore(nint monitor, nint data)
    {
        var list = (List<MonitorDescription>)GCHandle.FromIntPtr(data).Target!;

        var info = new NativeMonitorInfoEx { Size = (uint)sizeof(NativeMonitorInfoEx) };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return 1; // продолжить перечисление
        }

        string deviceName = info.GetDeviceName();
        (double? widthMm, double? heightMm) = QueryPhysicalSizeMm(deviceName);
        list.Add(new MonitorDescription(
            monitor,
            deviceName,
            new MonitorRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
            (info.Flags & NativeConstants.MonitorInfoFlagPrimary) != 0,
            widthMm,
            heightMm,
            QueryDpiScale(monitor)));
        return 1;
    }

    private static double QueryDpiScale(nint monitor)
    {
        try
        {
            int hr = NativeMethods.GetDpiForMonitor(monitor, NativeConstants.MdtEffectiveDpi, out uint dpiX, out _);
            return hr == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        }
        catch (DllNotFoundException)
        {
            return 1.0;
        }
        catch (EntryPointNotFoundException)
        {
            return 1.0;
        }
    }

    private static (double? WidthMm, double? HeightMm) QueryPhysicalSizeMm(string deviceName)
    {
        nint hdc = NativeMethods.CreateDC("DISPLAY", deviceName, null, 0);
        if (hdc == 0)
        {
            return (null, null);
        }

        try
        {
            int w = NativeMethods.GetDeviceCaps(hdc, NativeConstants.HorzSize);
            int h = NativeMethods.GetDeviceCaps(hdc, NativeConstants.VertSize);
            return w > 0 && h > 0 ? (w, h) : (null, null);
        }
        finally
        {
            NativeMethods.DeleteDC(hdc);
        }
    }
}
