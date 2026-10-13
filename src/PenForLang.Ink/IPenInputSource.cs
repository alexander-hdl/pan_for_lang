namespace PenForLang.Ink;

/// <summary>
/// Экранный прямоугольник холста в ФИЗИЧЕСКИХ пикселях экрана (ARCHITECTURE.md §7.3, A12).
/// Значения берутся из <c>canvas.PointToScreen(...)</c> без умножения на DPI-масштаб.
/// </summary>
/// <param name="OriginXPx">Левый край холста на экране, px.</param>
/// <param name="OriginYPx">Верхний край холста на экране, px.</param>
/// <param name="WidthPx">Ширина холста, px.</param>
/// <param name="HeightPx">Высота холста, px.</param>
public readonly record struct PenSurface(double OriginXPx, double OriginYPx, double WidthPx, double HeightPx)
{
    /// <summary>
    /// Строит прямоугольник по двум углам, полученным из <c>PointToScreen</c>: левый верхний (0,0) и правый нижний
    /// (ActualWidth, ActualHeight). Числа уже физические — на DPI они не умножаются (A12).
    /// </summary>
    /// <exception cref="ArgumentException">Числа не конечны или правый нижний угол левее/выше левого верхнего.</exception>
    public static PenSurface FromScreenCorners(double leftPx, double topPx, double rightPx, double bottomPx)
    {
        if (!double.IsFinite(leftPx) || !double.IsFinite(topPx) || !double.IsFinite(rightPx) || !double.IsFinite(bottomPx))
        {
            throw new ArgumentException("Corners must be finite.");
        }

        if (rightPx < leftPx || bottomPx < topPx)
        {
            throw new ArgumentException("The bottom-right corner must not precede the top-left corner.");
        }

        return new PenSurface(leftPx, topPx, rightPx - leftPx, bottomPx - topPx);
    }

    /// <summary>Лежит ли точка поверхности (координаты холста, px) внутри холста; границы включены.</summary>
    public bool ContainsSurfacePoint(double x, double y) => x >= 0 && y >= 0 && x <= WidthPx && y <= HeightPx;
}

/// <summary>
/// Источник ввода пера (ARCHITECTURE.md §3.7, §7.1). Платформенные реализации живут в <c>Platform.Windows</c>;
/// платформенно-независимые — <see cref="ReplayPenSource"/> и фейки в <c>PenForLang.Testing</c>.
/// </summary>
/// <remarks>
/// Касание и отрыв выражены фазами точек (<see cref="PenPhase.Down"/>, <see cref="PenPhase.Up"/>), указатель — <see cref="PenSample.PointerId"/>.
/// Владение указателем по <c>pointerId</c> (A11) реализует источник; чистая часть правила — <see cref="PointerOwnership"/>.
/// </remarks>
public interface IPenInputSource : IAsyncDisposable
{
    /// <summary>Вид источника.</summary>
    PenSourceKind Kind { get; }

    /// <summary>Описание устройства, если уже известно.</summary>
    PenDeviceInfo? Device { get; }

    /// <summary>
    /// Пачка точек в хронологическом порядке. Буфер переиспользуется после возврата из обработчика — копировать сразу.
    /// </summary>
    event Action<ReadOnlyMemory<PenSample>>? SamplesReceived;

    /// <summary>Событие устройства: подключение, потеря, смена монитора.</summary>
    event Action<PenDeviceEvent>? DeviceChanged;

    /// <summary>Задаёт экранный прямоугольник холста в ФИЗИЧЕСКИХ пикселях (из <c>PointToScreen</c>, A12).</summary>
    void SetSurface(PenSurface surface);

    /// <summary>Запускает источник.</summary>
    ValueTask StartAsync(CancellationToken ct);

    /// <summary>Останавливает источник; точки больше не поступают.</summary>
    ValueTask StopAsync();
}
