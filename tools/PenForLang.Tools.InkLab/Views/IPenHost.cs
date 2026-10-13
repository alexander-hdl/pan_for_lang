using PenForLang.Core;
using PenForLang.Ink;
using PenForLang.Platform.Windows;

namespace PenForLang.Tools.InkLab.Views;

/// <summary>Окно-хозяин источника пера для вкладки «Запись»: источник, часы, масштаб экрана и установка прямоугольника холста.</summary>
public interface IPenHost
{
    /// <summary>Источник пера (<c>Win32PointerPenSource</c> или <c>MousePenSource</c>).</summary>
    IPenInputSource Source { get; }

    /// <summary>Монотонные часы источника (QPC).</summary>
    IMonotonicClock Clock { get; }

    /// <summary>DPI-масштаб окна (1,0 = 96 dpi).</summary>
    double DpiScale { get; }

    /// <summary>Монитор, на котором стоит окно, если известен.</summary>
    MonitorDescription? Monitor { get; }

    /// <summary>Версия ОС для заголовка penlog.</summary>
    string OsVersion { get; }

    /// <summary>Версия приложения для заголовка penlog.</summary>
    string AppVersion { get; }

    /// <summary>Сообщает источнику экранный прямоугольник холста в физических px (<see cref="PenSurfaces.Hidden"/> — холст скрыт, перо не перехватывается).</summary>
    void SetSurface(PenSurface surface);
}

/// <summary>Служебные прямоугольники холста для <see cref="IPenHost.SetSurface"/>.</summary>
public static class PenSurfaces
{
    /// <summary>
    /// Прямоугольник 1×1 далеко за пределами экрана (источник требует положительный размер): пока вкладка записи скрыта,
    /// ни одно касание не попадает внутрь холста и перо работает в интерфейсе как мышь.
    /// </summary>
    public static PenSurface Hidden { get; } = new(-32000, -32000, 1, 1);
}
