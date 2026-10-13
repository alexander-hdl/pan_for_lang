using System.Collections.Immutable;

namespace PenForLang.Evaluation;

/// <summary>
/// Метрики одного сопоставления «штрих рукописи ↔ основной штрих эталона» (ARCHITECTURE.md §6.4.9). Все длины — в x-height
/// линейки; метрики формы посчитаны после нормализации (наклон, масштаб, сдвиг; §6.4.2, проход 1), метрики достижения
/// линий — в абсолютных единицах линейки (проход 2).
/// </summary>
/// <param name="StrokeId">Штрих эталона.</param>
/// <param name="InkStrokeIndex"><see cref="PenForLang.Ink.RulerStroke.Index"/> штриха рукописи.</param>
/// <param name="Reversed">Обращённый эталон лучше прямого по стоимости DTW (штрих написан в обратную сторону, P2).</param>
/// <param name="MatchCost">Стоимость DTW на точку рукописи (позиция² + направление) для выбранного направления.</param>
/// <param name="StartErr">Расстояние первой точки до ближайшего допустимого старта (подход leadIn, его продолжение влево или начало тела; для обратного — конец эталона).</param>
/// <param name="StartArcOffset">Длина дуги вдоль тела эталона от его начала до места, с которого DTW «начинает» рукопись (0 — на подходе или в начале тела).</param>
/// <param name="ReverseCostRatio">Отношение стоимости обращённого выравнивания к прямому (меньше 1 — обратное лучше).</param>
/// <param name="FirstSegmentAngleDeg">Угол между первым участком рукописи (0,2 x-h) и допустимым направлением старта, градусы.</param>
/// <param name="DtwMean">Среднее по точкам рукописи расстояние до выровненной точки эталона.</param>
/// <param name="DtwMax">Наибольшее такое расстояние.</param>
/// <param name="Frechet">Дискретное расстояние Фреше до участка эталона, покрытого путём DTW.</param>
/// <param name="CorridorShare">Доля точек рукописи в коридоре эталона.</param>
/// <param name="Coverage">Доля длины тела эталона, покрытая рукописью в коридоре.</param>
/// <param name="ClosedGap">Превышение зазора замыкания овала над эталонным (x-height) или <c>null</c>, если проверки нет.</param>
/// <param name="LoopOk">Нужная петля есть или <c>null</c>, если проверки нет.</param>
/// <param name="RetraceShare">Доля ретрейса, лежащая по написанному, относительно эталона или <c>null</c>, если проверки нет или она неприменима.</param>
/// <param name="StartOk">Старт в допуске.</param>
/// <param name="DirectionOk">Направление верно (штрих в целом и обход овалов и петель).</param>
/// <param name="RangeAreaRatio">Отношение ориентированных площадей участка (овал, петля) рукописи и эталона, если обход участка обратный (отрицательное), иначе <c>null</c>.</param>
public sealed record StrokeMatch(
    string StrokeId,
    int InkStrokeIndex,
    bool Reversed,
    double MatchCost,
    double StartErr,
    double StartArcOffset,
    double ReverseCostRatio,
    double FirstSegmentAngleDeg,
    double DtwMean,
    double DtwMax,
    double Frechet,
    double CorridorShare,
    double Coverage,
    double? ClosedGap,
    bool? LoopOk,
    double? RetraceShare,
    bool StartOk,
    bool DirectionOk,
    double? RangeAreaRatio);

/// <summary>Сопоставление штриха рукописи со слотом отложенного штриха.</summary>
/// <param name="SlotId">Идентификатор слота в глифе.</param>
/// <param name="InkStrokeIndex">Штрих рукописи или <c>null</c>, если слот пуст.</param>
/// <param name="Distance">Расстояние от середины штриха рукописи до ожидаемого положения (по реальному стволу), x-height, или <c>null</c>.</param>
/// <param name="Timing">Когда поставлен.</param>
public sealed record DeferredMatch(string SlotId, int? InkStrokeIndex, double? Distance, DeferredTiming Timing);

/// <summary>Вклад метрики в <c>form</c> (ARCHITECTURE.md §6.4.13).</summary>
/// <param name="Name">Имя метрики (ключ в <c>scoring.json → form.weights</c>).</param>
/// <param name="Value">Значение метрики.</param>
/// <param name="Score">Подбалл, [0, 1].</param>
/// <param name="Weight">Нормированный вес среди применимых метрик; сумма весов равна 1.</param>
public sealed record FormComponent(string Name, double Value, double Score, double Weight);

/// <summary>Параметры нормализации рукописи к эталону для выравнивания (§6.4.2, проход 1). Нормализация не к bounding box.</summary>
/// <param name="ShearDeg">Деслантинг рукописи к наклону эталона, градусы (0 — не применялся).</param>
/// <param name="Scale">Масштаб рукописи относительно эталона по робастной высоте (1 — не применялся).</param>
/// <param name="ShiftU">Итоговый сдвиг по u, x-height.</param>
/// <param name="ShiftV">Сдвиг по v (нормализация baseline), x-height.</param>
/// <param name="InkSlantDeg">Оценка наклона рукописи (медиана нисходящих квазивертикалей) или <c>null</c>.</param>
/// <param name="TemplateSlantDeg">То же для эталона или <c>null</c>.</param>
public sealed record NormalizationInfo(double ShearDeg, double Scale, double ShiftU, double ShiftV, double? InkSlantDeg, double? TemplateSlantDeg);

/// <summary>
/// Детализация результата (v0-provisional, ARCHITECTURE.md §6.2): сопоставления штрихов, подбаллы формы, нормализация и
/// диагностика. Изменяется свободно до решения T0.8; потребители, кроме UI и InkLab, на неё не опираются.
/// </summary>
/// <param name="Strokes">Сопоставленные основные штрихи в порядке эталона.</param>
/// <param name="Deferred">Сопоставления слотов отложенных штрихов в порядке глифа.</param>
/// <param name="FormComponents">Подбаллы формы и веса в порядке <c>form.weights</c>.</param>
/// <param name="Normalization">Параметры нормализации.</param>
/// <param name="Diagnostics">Прочие числа (отсортированы по имени, порядок не зависит от платформы).</param>
public sealed record EvaluationDetail(
    ImmutableArray<StrokeMatch> Strokes,
    ImmutableArray<DeferredMatch> Deferred,
    ImmutableArray<FormComponent> FormComponents,
    NormalizationInfo Normalization,
    ImmutableSortedDictionary<string, double> Diagnostics);
