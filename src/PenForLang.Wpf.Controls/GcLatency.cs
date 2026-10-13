using System.Runtime;

namespace PenForLang.Wpf.Controls;

/// <summary>
/// Абстракция режима задержки сборщика мусора (<see cref="GCSettings.LatencyMode"/>): чтобы включение
/// <see cref="GCLatencyMode.SustainedLowLatency"/> на время попытки (ARCHITECTURE.md §2.5, A32) проверялось тестом без изменения
/// настроек процесса.
/// </summary>
public interface IGcLatencyMode
{
    /// <summary>Текущий режим; запись может бросить <see cref="InvalidOperationException"/>, если режим недопустим.</summary>
    GCLatencyMode Mode { get; set; }
}

/// <summary>Режим сборщика мусора процесса (<see cref="GCSettings.LatencyMode"/>).</summary>
public sealed class SystemGcLatencyMode : IGcLatencyMode
{
    /// <inheritdoc />
    public GCLatencyMode Mode
    {
        get => GCSettings.LatencyMode;
        set => GCSettings.LatencyMode = value;
    }
}

/// <summary>
/// Включает <see cref="GCLatencyMode.SustainedLowLatency"/> на время попытки письма и возвращает прежний режим после неё.
/// Повторные <see cref="Begin"/> и <see cref="End"/> безопасны (идемпотентны). Не потокобезопасен — работает в потоке UI.
/// </summary>
public sealed class AttemptGcLatencyGuard
{
    private readonly IGcLatencyMode _gc;
    private GCLatencyMode _previous;

    /// <summary>Создаёт сторож.</summary>
    /// <param name="gc">Управляемый режим сборщика мусора.</param>
    public AttemptGcLatencyGuard(IGcLatencyMode gc)
    {
        ArgumentNullException.ThrowIfNull(gc);
        _gc = gc;
    }

    /// <summary>Режим задержки включён этим сторожем.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Режим, который был до <see cref="Begin"/> (имеет смысл при <see cref="IsActive"/>).</summary>
    public GCLatencyMode Previous => _previous;

    /// <summary>Сколько раз режим был включён за время жизни сторожа.</summary>
    public int ActivationCount { get; private set; }

    /// <summary>Включает режим. Возвращает <c>true</c>, если режим включён (в том числе уже ранее).</summary>
    public bool Begin()
    {
        if (IsActive)
        {
            return true;
        }

        try
        {
            _previous = _gc.Mode;
            _gc.Mode = GCLatencyMode.SustainedLowLatency;
        }
        catch (InvalidOperationException)
        {
            // Режим недоступен (например, отключён параллельный сборщик): попытка идёт без него.
            return false;
        }

        IsActive = true;
        ActivationCount++;
        return true;
    }

    /// <summary>Возвращает прежний режим; без предшествующего успешного <see cref="Begin"/> ничего не делает.</summary>
    public void End()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        try
        {
            _gc.Mode = _previous;
        }
        catch (InvalidOperationException)
        {
            // Нечего восстанавливать: режим уже изменён извне.
        }
    }
}
