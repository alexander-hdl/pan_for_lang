using System.Windows;
using PenForLang.Ink;

namespace PenForLang.Wpf.Controls;

/// <summary>
/// Программная растровая поверхность живых чернил (ARCHITECTURE.md §7.5, ADR-004): принимает точки пера, сглаживает их, рисует
/// штрихи (закруглённые отрезки переменной толщины с антиалиасингом) прямо в массив пикселей <c>Pbgra32</c> и копит изменённый
/// прямоугольник. К WPF (кроме структуры <see cref="Int32Rect"/>) поверхность не обращается, поэтому её горячий путь
/// <see cref="Add"/> не аллоцирует и проверяется обычным тестом.
/// </summary>
/// <remarks>
/// <b>Почему не DrawingVisual/StreamGeometry.</b> Каждое обновление <c>DrawingVisual</c> требует <c>RenderOpen()</c> (новый
/// <c>DrawingContext</c>), каждая геометрия — <c>StreamGeometry.Open()</c> (новый контекст) и <c>Freeze</c>; это неустранимые
/// аллокации внутри WPF, то есть требование «горячий путь без аллокаций» (A32) на этой технике невыполнимо. Здесь растеризация идёт в
/// заранее выделенный закреплённый массив, а в <c>WriteableBitmap</c> изменённая область переносится один раз за кадр
/// (<c>LiveInkLayer</c>, <c>CompositionTarget.Rendering</c>) — пачки точек между кадрами всё равно показываются не раньше следующего.
/// <para><b>Что хранится.</b> Сглаженные точки штрихов (X, Y, радиус) — для пересборки при смене размера/DPI и для счётчиков.
/// Массивы выделены заранее (<see cref="InitialPointCapacity"/> точек) и растут вдвое только при переполнении, то есть вне
/// горячего пути в обычной попытке.</para>
/// <para><b>Сглаживание.</b> Адаптивное экспоненциальное: чем дальше сырая точка от сглаженной, тем сильнее она «тянет» (малый шум
/// подавляется, быстрое движение почти не отстаёт); на отрыве штрих дорисовывается до настоящей последней точки.</para>
/// <para><b>Толщина.</b> <see cref="BaseRadiusPx"/> × коэффициент давления в пределах [<see cref="MinPressureFactor"/>,
/// <see cref="MaxPressureFactor"/>], изменение радиуса между соседними точками ограничено (<see cref="MaxRadiusStepFraction"/>).</para>
/// Не потокобезопасен — работает в потоке UI.
/// </remarks>
public sealed class InkSurface
{
    /// <summary>Сколько точек выделяется заранее (около 80 с непрерывного письма при 200 Гц).</summary>
    public const int InitialPointCapacity = 16384;

    /// <summary>Сколько штрихов выделяется заранее.</summary>
    public const int InitialStrokeCapacity = 512;

    private const float MinRadiusPx = 0.6f;

    private readonly uint[] _alphaToPixel = new uint[256];

    private uint[] _pixels;
    private int _width;
    private int _height;

    private float[] _pointX = new float[InitialPointCapacity];
    private float[] _pointY = new float[InitialPointCapacity];
    private float[] _pointR = new float[InitialPointCapacity];
    private int _pointCount;
    private int[] _strokeFirstPoint = new int[InitialStrokeCapacity];
    private int _strokeCount;

    private bool _open;
    private float _smoothX;
    private float _smoothY;
    private float _smoothPressure;
    private float _anchorX;
    private float _anchorY;
    private float _anchorR;

    private int _dirtyMinX;
    private int _dirtyMinY;
    private int _dirtyMaxX;
    private int _dirtyMaxY;
    private bool _dirty;

    private bool _pending;
    private long _pendingNewestUs;
    private long _pendingOldestUs;
    private long _pendingFirstArrivalUs;
    private long _pendingLatestArrivalUs;

    /// <summary>Создаёт поверхность.</summary>
    /// <param name="widthPx">Ширина, физические px; &gt; 0.</param>
    /// <param name="heightPx">Высота, физические px; &gt; 0.</param>
    public InkSurface(int widthPx, int heightPx)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPx);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPx);
        _width = widthPx;
        _height = heightPx;
        _pixels = GC.AllocateArray<uint>(widthPx * heightPx, pinned: true);
        SetInkColor(0xFF, 0x1A, 0x2B, 0x6B);
    }

    /// <summary>Ширина, px.</summary>
    public int WidthPx => _width;

    /// <summary>Высота, px.</summary>
    public int HeightPx => _height;

    /// <summary>Радиус пера при давлении, равном единице коэффициента, px (половина ширины линии).</summary>
    public float BaseRadiusPx { get; set; } = 3.0f;

    /// <summary>
    /// Толщина зависит от давления (иначе радиус постоянен и равен <see cref="BaseRadiusPx"/>). По умолчанию выключено: методика не даёт
    /// видимой обратной связи по давлению (ARCHITECTURE.md §7.5, М D15/§9.1); включается только разработчиком.
    /// </summary>
    public bool PressureEnabled { get; set; }

    /// <summary>Коэффициент радиуса при нулевом давлении.</summary>
    public float MinPressureFactor { get; set; } = 0.55f;

    /// <summary>Коэффициент радиуса при полном давлении.</summary>
    public float MaxPressureFactor { get; set; } = 1.45f;

    /// <summary>Давление, принимаемое вместо нуля в точке контакта (у части устройств в Down давление ещё нулевое).</summary>
    public float DefaultPressure { get; set; } = 0.5f;

    /// <summary>Наибольшее изменение радиуса между соседними точками, доля <see cref="BaseRadiusPx"/>.</summary>
    public float MaxRadiusStepFraction { get; set; } = 0.12f;

    /// <summary>Слабейшее сглаживание (доля сдвига сглаженной точки к сырой при малом отклонении), 0..1.</summary>
    public float MinAlpha { get; set; } = 0.4f;

    /// <summary>Сильнейшее сглаживание (при большом отклонении), 0..1.</summary>
    public float MaxAlpha { get; set; } = 0.9f;

    /// <summary>Отклонение сырой точки от сглаженной (px), при котором доля сдвига достигает <see cref="MaxAlpha"/>.</summary>
    public float FollowDistancePx { get; set; } = 8.0f;

    /// <summary>Отрезки короче этого (px) не рисуются и не сохраняются.</summary>
    public float MinSegmentPx { get; set; } = 0.35f;

    /// <summary>Сколько штрихов начато с последней очистки.</summary>
    public int StrokeCount => _strokeCount;

    /// <summary>Сколько отрезков нарисовано (без начальных точек штрихов).</summary>
    public int SegmentCount { get; private set; }

    /// <summary>Сколько сглаженных точек сохранено.</summary>
    public int PointCount => _pointCount;

    /// <summary>
    /// Очистить поверхность перед началом следующего штриха («Готово» оставляет чернила на экране до следующего касания).
    /// Сбрасывается после очистки.
    /// </summary>
    public bool ClearBeforeNextStroke { get; set; }

    /// <summary>Идёт штрих (получен Down, ещё нет Up).</summary>
    public bool IsPenDown => _open;

    /// <summary>X последней точки (контакт или наведение), px поверхности.</summary>
    public float TipX { get; private set; }

    /// <summary>Y последней точки, px поверхности.</summary>
    public float TipY { get; private set; }

    /// <summary>Была ли хотя бы одна точка (положение пера известно).</summary>
    public bool HasTip { get; private set; }

    /// <summary>Были ли изменения пикселей с последнего <see cref="TryTakeDirty"/>.</summary>
    public bool IsDirty => _dirty;

    /// <summary>Пиксели <c>Pbgra32</c> построчно, ширина = <see cref="WidthPx"/>.</summary>
    public ReadOnlySpan<uint> Pixels => _pixels;

    /// <summary>Закреплённый массив пикселей (для передачи в <c>WriteableBitmap</c> без копирования и аллокаций).</summary>
    internal uint[] PixelArray => _pixels;

    /// <summary>Есть ли точки, ожидающие показа (для измерения задержки).</summary>
    public bool HasPending => _pending;

    /// <summary>Метка времени новейшей точки, ожидающей показа, мкс (шкала источника).</summary>
    public long PendingNewestPointUs => _pendingNewestUs;

    /// <summary>Метка времени старейшей точки, ожидающей показа, мкс.</summary>
    public long PendingOldestPointUs => _pendingOldestUs;

    /// <summary>Момент (мкс, шкала часов <c>Add</c>), когда получена пачка со старейшей из ожидающих точек.</summary>
    public long PendingFirstArrivalUs => _pendingFirstArrivalUs;

    /// <summary>Момент получения пачки с новейшей из ожидающих точек (та же точка, что и <see cref="PendingNewestPointUs"/>).</summary>
    public long PendingLatestArrivalUs => _pendingLatestArrivalUs;

    /// <summary>Цвет чернил (непрозрачный).</summary>
    public void SetInkColor(byte alpha, byte red, byte green, byte blue)
    {
        for (int a = 0; a < 256; a++)
        {
            // Цвет с общей прозрачностью alpha, покрытие a/255: предумноженные компоненты.
            int coverage = (a * alpha + 127) / 255;
            uint r = (uint)(((red * coverage) + 127) / 255);
            uint g = (uint)(((green * coverage) + 127) / 255);
            uint b = (uint)(((blue * coverage) + 127) / 255);
            _alphaToPixel[a] = ((uint)coverage << 24) | (r << 16) | (g << 8) | b;
        }
    }

    /// <summary>
    /// Принимает пачку точек (координаты — физические px поверхности, в порядке времени). Горячий путь: аллокаций нет.
    /// </summary>
    /// <param name="samples">Точки пачки.</param>
    /// <param name="arrivalUs">Момент получения пачки, мкс (для измерения задержки; 0, если не измеряется).</param>
    public void Add(ReadOnlySpan<PenSample> samples, long arrivalUs = 0)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        foreach (ref readonly PenSample s in samples)
        {
            Process(in s);
        }

        TrackPending(samples, arrivalUs);
    }

    /// <summary>Стирает чернила и сбрасывает счётчики (кнопка «Заново»).</summary>
    public void Clear()
    {
        Array.Clear(_pixels);
        _pointCount = 0;
        _strokeCount = 0;
        SegmentCount = 0;
        _open = false;
        MarkDirty(0, 0, _width - 1, _height - 1);
    }

    /// <summary>Сбрасывает накопленную информацию о задержке (после показа кадра).</summary>
    public void ClearPending() => _pending = false;

    /// <summary>
    /// Меняет размер и перерисовывает сохранённые штрихи (координаты поверхности — физические px, поэтому смена DPI сама по себе
    /// их не меняет; перерисовка нужна при новом размере буфера). Вызывается вне горячего пути.
    /// </summary>
    public void Resize(int widthPx, int heightPx)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPx);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPx);
        if (widthPx == _width && heightPx == _height)
        {
            return;
        }

        _width = widthPx;
        _height = heightPx;
        _pixels = GC.AllocateArray<uint>(widthPx * heightPx, pinned: true);
        Redraw();
        MarkDirty(0, 0, _width - 1, _height - 1);
    }

    /// <summary>Забирает изменённый прямоугольник и сбрасывает признак изменений.</summary>
    /// <param name="rect">Прямоугольник в px буфера.</param>
    /// <returns><c>false</c>, если изменений не было.</returns>
    public bool TryTakeDirty(out Int32Rect rect)
    {
        if (!_dirty)
        {
            rect = default;
            return false;
        }

        rect = new Int32Rect(_dirtyMinX, _dirtyMinY, _dirtyMaxX - _dirtyMinX + 1, _dirtyMaxY - _dirtyMinY + 1);
        _dirty = false;
        return true;
    }

    /// <summary>Радиус пера для давления <paramref name="pressure"/>, px (без ограничения скорости изменения).</summary>
    public float RadiusFor(float pressure)
    {
        if (!PressureEnabled)
        {
            return Math.Max(BaseRadiusPx, MinRadiusPx);
        }

        float p = pressure > 0f ? Math.Min(pressure, 1f) : DefaultPressure;
        float factor = MinPressureFactor + ((MaxPressureFactor - MinPressureFactor) * p);
        return Math.Max(BaseRadiusPx * factor, MinRadiusPx);
    }

    private void Process(in PenSample s)
    {
        float x = (float)s.X;
        float y = (float)s.Y;
        TipX = x;
        TipY = y;
        HasTip = true;

        if (s.Phase == PenPhase.Hover)
        {
            return;
        }

        if (s.IsAuxiliary)
        {
            // Барабанная кнопка и ластик не рисуют (§7.2): штрих прерывается без дорисовки.
            _open = false;
            return;
        }

        switch (s.Phase)
        {
            case PenPhase.Down:
                BeginStroke(x, y, s.Pressure);
                break;
            case PenPhase.Move:
                if (_open)
                {
                    Extend(x, y, s.Pressure);
                }

                break;
            case PenPhase.Up:
                if (_open)
                {
                    Finish(x, y);
                }

                break;
            default:
                break;
        }
    }

    private void BeginStroke(float x, float y, float pressure)
    {
        if (ClearBeforeNextStroke)
        {
            ClearBeforeNextStroke = false;
            Clear();
        }

        if (_strokeCount == _strokeFirstPoint.Length)
        {
            Array.Resize(ref _strokeFirstPoint, _strokeFirstPoint.Length * 2);
        }

        _strokeFirstPoint[_strokeCount++] = _pointCount;
        _open = true;
        _smoothX = x;
        _smoothY = y;
        _smoothPressure = pressure > 0f ? pressure : DefaultPressure;
        float r = RadiusFor(pressure);
        AddPoint(x, y, r);
        Capsule(x, y, r, x, y, r);
        _anchorX = x;
        _anchorY = y;
        _anchorR = r;
    }

    private void Extend(float x, float y, float pressure)
    {
        float dx = x - _smoothX;
        float dy = y - _smoothY;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy));
        float follow = FollowDistancePx > 0 ? Math.Min(1f, distance / FollowDistancePx) : 1f;
        float alpha = MinAlpha + ((MaxAlpha - MinAlpha) * follow);
        _smoothX += dx * alpha;
        _smoothY += dy * alpha;
        _smoothPressure += ((pressure > 0f ? pressure : DefaultPressure) - _smoothPressure) * alpha;

        float ax = _smoothX - _anchorX;
        float ay = _smoothY - _anchorY;
        if (((ax * ax) + (ay * ay)) < (MinSegmentPx * MinSegmentPx))
        {
            return;
        }

        float r = LimitRadius(RadiusFor(_smoothPressure), _anchorR);
        AddPoint(_smoothX, _smoothY, r);
        Capsule(_anchorX, _anchorY, _anchorR, _smoothX, _smoothY, r);
        SegmentCount++;
        _anchorX = _smoothX;
        _anchorY = _smoothY;
        _anchorR = r;
    }

    private void Finish(float x, float y)
    {
        float ax = x - _anchorX;
        float ay = y - _anchorY;
        if (((ax * ax) + (ay * ay)) >= 0.01f)
        {
            // Сглаженная точка отстаёт от пера: дорисовываем штрих до настоящего конца с прежним радиусом.
            AddPoint(x, y, _anchorR);
            Capsule(_anchorX, _anchorY, _anchorR, x, y, _anchorR);
            SegmentCount++;
            _anchorX = x;
            _anchorY = y;
        }

        _open = false;
    }

    private float LimitRadius(float target, float previous)
    {
        float step = Math.Max(BaseRadiusPx * MaxRadiusStepFraction, 0.05f);
        return Math.Clamp(target, previous - step, previous + step);
    }

    private void AddPoint(float x, float y, float r)
    {
        if (_pointCount == _pointX.Length)
        {
            int size = _pointX.Length * 2;
            Array.Resize(ref _pointX, size);
            Array.Resize(ref _pointY, size);
            Array.Resize(ref _pointR, size);
        }

        _pointX[_pointCount] = x;
        _pointY[_pointCount] = y;
        _pointR[_pointCount] = r;
        _pointCount++;
    }

    private void Redraw()
    {
        for (int stroke = 0; stroke < _strokeCount; stroke++)
        {
            int first = _strokeFirstPoint[stroke];
            int end = stroke + 1 < _strokeCount ? _strokeFirstPoint[stroke + 1] : _pointCount;
            if (first >= end)
            {
                continue;
            }

            Capsule(_pointX[first], _pointY[first], _pointR[first], _pointX[first], _pointY[first], _pointR[first]);
            for (int i = first + 1; i < end; i++)
            {
                Capsule(_pointX[i - 1], _pointY[i - 1], _pointR[i - 1], _pointX[i], _pointY[i], _pointR[i]);
            }
        }
    }

    private void TrackPending(ReadOnlySpan<PenSample> samples, long arrivalUs)
    {
        long oldest = samples[0].TimeUs;
        long newest = samples[^1].TimeUs;
        if (!_pending)
        {
            _pending = true;
            _pendingOldestUs = oldest;
            _pendingNewestUs = newest;
            _pendingFirstArrivalUs = arrivalUs;
            _pendingLatestArrivalUs = arrivalUs;
            return;
        }

        if (oldest < _pendingOldestUs)
        {
            _pendingOldestUs = oldest;
        }

        if (newest >= _pendingNewestUs)
        {
            _pendingNewestUs = newest;
            _pendingLatestArrivalUs = arrivalUs;
        }
    }

    private void MarkDirty(int minX, int minY, int maxX, int maxY)
    {
        if (!_dirty)
        {
            _dirtyMinX = minX;
            _dirtyMinY = minY;
            _dirtyMaxX = maxX;
            _dirtyMaxY = maxY;
            _dirty = true;
            return;
        }

        if (minX < _dirtyMinX)
        {
            _dirtyMinX = minX;
        }

        if (minY < _dirtyMinY)
        {
            _dirtyMinY = minY;
        }

        if (maxX > _dirtyMaxX)
        {
            _dirtyMaxX = maxX;
        }

        if (maxY > _dirtyMaxY)
        {
            _dirtyMaxY = maxY;
        }
    }

    /// <summary>
    /// Закрашивает «капсулу» — отрезок с радиусом, линейно меняющимся от <paramref name="r0"/> к <paramref name="r1"/>, и круглыми
    /// концами. Покрытие пикселя — расстояние от его центра до оси, сведённое к 0..1 (антиалиасинг в один пиксель). Перекрытия
    /// объединяются по максимуму покрытия, поэтому стыки отрезков не темнеют.
    /// </summary>
    private void Capsule(float x0, float y0, float r0, float x1, float y1, float r1)
    {
        float reach = Math.Max(r0, r1) + 1f;
        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(x0, x1) - reach));
        int maxX = Math.Min(_width - 1, (int)MathF.Ceiling(Math.Max(x0, x1) + reach));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(y0, y1) - reach));
        int maxY = Math.Min(_height - 1, (int)MathF.Ceiling(Math.Max(y0, y1) + reach));
        if (minX > maxX || minY > maxY)
        {
            return;
        }

        float abx = x1 - x0;
        float aby = y1 - y0;
        float lengthSquared = (abx * abx) + (aby * aby);
        float inverse = lengthSquared > 1e-6f ? 1f / lengthSquared : 0f;
        float dr = r1 - r0;

        uint[] pixels = _pixels;
        uint[] lut = _alphaToPixel;
        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            int row = y * _width;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float t = (((px - x0) * abx) + ((py - y0) * aby)) * inverse;
                t = t < 0f ? 0f : (t > 1f ? 1f : t);
                float cx = x0 + (abx * t);
                float cy = y0 + (aby * t);
                float ex = px - cx;
                float ey = py - cy;
                float coverage = r0 + (dr * t) - MathF.Sqrt((ex * ex) + (ey * ey)) + 0.5f;
                if (coverage <= 0f)
                {
                    continue;
                }

                int alpha = coverage >= 1f ? 255 : (int)((coverage * 255f) + 0.5f);
                uint value = lut[alpha];
                int index = row + x;
                if ((pixels[index] >> 24) < (value >> 24))
                {
                    pixels[index] = value;
                }
            }
        }

        MarkDirty(minX, minY, maxX, maxY);
    }
}
