using System.Collections.Immutable;
using PenForLang.Core.Geometry;

namespace PenForLang.Evaluation.Config;

/// <summary>
/// Кусочно-линейная функция подбалла (ARCHITECTURE.md §6.4.13): точки <c>(x, y)</c> с возрастающим <c>x</c>;
/// слева от первой и справа от последней точки значение постоянно. Мёртвая зона — горизонтальный участок в начале.
/// </summary>
/// <param name="Points">Точки (x — значение метрики, y — подбалл в [0, 1]).</param>
/// <param name="ToleranceScaled">Ось x масштабируется допуском профиля (метрики-ошибки в x-height): <c>y = f(x / scale)</c>.</param>
public sealed record ScoreFunction(ImmutableArray<Vec2> Points, bool ToleranceScaled)
{
    /// <summary>Значение функции в точке <paramref name="x"/> при множителе допуска <paramref name="toleranceScale"/> (&gt; 0).</summary>
    /// <param name="x">Значение метрики.</param>
    /// <param name="toleranceScale">Множитель допуска профиля; для функций без масштабирования не используется.</param>
    public double Evaluate(double x, double toleranceScale)
    {
        double v = ToleranceScaled ? x / toleranceScale : x;
        ImmutableArray<Vec2> p = Points;
        if (v <= p[0].X)
        {
            return p[0].Y;
        }

        for (int i = 1; i < p.Length; i++)
        {
            if (v <= p[i].X)
            {
                double span = p[i].X - p[i - 1].X;
                return p[i - 1].Y + ((p[i].Y - p[i - 1].Y) * ((v - p[i - 1].X) / span));
            }
        }

        return p[^1].Y;
    }
}

/// <summary>Сглаживание Савицкого–Голея (окно и порядок).</summary>
/// <param name="Window">Нечётное окно в точках ресэмплированного штриха.</param>
/// <param name="Order">Порядок многочлена.</param>
public sealed record SmoothingConfig(int Window, int Order);

/// <summary>Предобработка (§6.4.1).</summary>
/// <param name="SpikeJumpMedianSteps">Точка-выброс: оба шага к ней длиннее стольких медианных шагов, а сосед-к-соседу близко.</param>
/// <param name="DotMaxLength">Микроштрих: длина меньше (x-height) ...</param>
/// <param name="DotMaxDurationMs">... и длительность меньше (мс) — <c>dotCandidate</c>.</param>
/// <param name="MinPointDistance">Прореживание: точки ближе этого к последней оставленной отбрасываются (подавляет дрожание на уровне отсчётов).</param>
public sealed record PreprocessConfig(double SpikeJumpMedianSteps, double DotMaxLength, double DotMaxDurationMs, double MinPointDistance);

/// <summary>Деслантинг рукописи к наклону эталона.</summary>
/// <param name="Enabled">Включён.</param>
/// <param name="DeadbandDeg">Разница наклонов меньше этого не исправляется (оценка наклона шумит).</param>
/// <param name="MaxDeg">Наибольшая поправка, градусы.</param>
/// <param name="MinRunLength">Минимальная длина участка для оценки наклона, x-height.</param>
/// <param name="MaxDeviationDeg">Допустимое отклонение хорды от вертикали при оценке, градусы.</param>
/// <param name="MaxSpreadDeg">Если углы участков расходятся больше (стандартное отклонение, градусы), наклон не определён (x, K) и деслантинг не применяется.</param>
public sealed record DeslantConfig(bool Enabled, double DeadbandDeg, double MaxDeg, double MinRunLength, double MaxDeviationDeg, double MaxSpreadDeg);

/// <summary>Масштаб рукописи для выравнивания (по робастной высоте, не по bounding box).</summary>
/// <param name="Enabled">Включён.</param>
/// <param name="Min">Наименьший масштаб.</param>
/// <param name="Max">Наибольший масштаб.</param>
/// <param name="LowPercentile">Нижний процентиль v, (0, 0,5).</param>
/// <param name="HighPercentile">Верхний процентиль v, (0,5, 1).</param>
public sealed record ScaleConfig(bool Enabled, double Min, double Max, double LowPercentile, double HighPercentile);

/// <summary>Нормализация для выравнивания (§6.4.2, проход 1).</summary>
/// <param name="DominantStrokeShare">Штрихи длиннее этой доли самого длинного определяют наклон, масштаб и сдвиг.</param>
/// <param name="Deslant">Деслантинг.</param>
/// <param name="Scale">Масштаб.</param>
/// <param name="Refine">Уточнение масштаба и сдвига по соответствиям выравнивания.</param>
public sealed record NormalizationConfig(
    double DominantStrokeShare,
    DeslantConfig Deslant,
    ScaleConfig Scale,
    RefineConfig Refine);

/// <summary>
/// Уточнение нормализации после первого выравнивания (робастный метод наименьших квадратов для масштаба и сдвига; §6.4.2).
/// </summary>
/// <param name="Enabled">Включено.</param>
/// <param name="Iterations">Число итераций отсечения худших соответствий.</param>
/// <param name="KeepShare">Доля лучших соответствий, участвующих в подгонке, (0, 1].</param>
/// <param name="ScaleMin">Наименьшая поправка масштаба.</param>
/// <param name="ScaleMax">Наибольшая поправка масштаба.</param>
/// <param name="MaxShiftV">Наибольшая поправка сдвига по v (baseline), x-height.</param>
/// <param name="MinChange">Поправка меньше этого (по масштабу и сдвигу) не применяется, и выравнивание не повторяется.</param>
/// <param name="Rounds">Наибольшее число раундов «выравнивание — подгонка».</param>
/// <param name="RerunAbove">Если поправка раунда больше этого, выравнивание повторяется ещё раз (большой сдвиг делает первые соответствия неточными).</param>
public sealed record RefineConfig(
    bool Enabled,
    int Iterations,
    double KeepShare,
    double ScaleMin,
    double ScaleMax,
    double MaxShiftV,
    double MinChange,
    int Rounds,
    double RerunAbove);

/// <summary>Веса локальной стоимости DTW (§6.4.4): <c>wPos·‖p−q‖² + wTheta·(1 − cos Δθ)</c>.</summary>
/// <param name="PositionWeight">Вес позиции.</param>
/// <param name="DirectionWeight">Вес направления.</param>
public sealed record DtwConfig(double PositionWeight, double DirectionWeight);

/// <summary>Сопоставление штрихов Венгерским алгоритмом с неназначением (§6.4.10).</summary>
/// <param name="ExtraStrokeCost">Цена неназначенного штриха рукописи.</param>
/// <param name="MissingStrokeCost">Цена неназначенного штриха эталона. Пара назначается, только если её стоимость меньше суммы обеих цен.</param>
/// <param name="MinLengthRatio">Штрих короче этой доли длины штриха эталона с ним не сопоставляется.</param>
/// <param name="MaxLengthRatio">Штрих длиннее этой доли — тоже.</param>
public sealed record MatchingConfig(double ExtraStrokeCost, double MissingStrokeCost, double MinLengthRatio, double MaxLengthRatio);

/// <summary>Параметры профиля допусков.</summary>
/// <param name="ToleranceScale">Множитель допусков формы (больше 1 — мягче).</param>
public sealed record ProfileConfig(double ToleranceScale);

/// <summary>Проверка направления (P2).</summary>
/// <param name="ReverseCostRatioMax">P2, если стоимость обращённого эталона не больше этой доли прямой.</param>
/// <param name="StrongReverseCostRatio">При отношении не больше этого обратное направление очевидно, и угол первого участка не проверяется.</param>
/// <param name="FirstSegmentAngleMaxDeg">... и первый участок рукописи отклонён от допустимого направления старта больше (градусы).</param>
/// <param name="FirstSegmentLength">Длина первого участка, x-height.</param>
/// <param name="MinStrokeLength">Штрих короче этого направление не проверяется (точка, короткая черта), x-height.</param>
/// <param name="BarMinDirectionalShare">Отложенная перекладина <c>ltr</c> написана справа налево (P2), если чистый сдвиг назад больше этой доли длины.</param>
public sealed record DirectionConfig(
    double ReverseCostRatioMax,
    double StrongReverseCostRatio,
    double FirstSegmentAngleMaxDeg,
    double FirstSegmentLength,
    double MinStrokeLength,
    double BarMinDirectionalShare);

/// <summary>Число основных штрихов (P4).</summary>
/// <param name="ExtraForP4">Лишних штрихов для находки P4.</param>
/// <param name="ExtraForStop">Лишних штрихов для стоп-ошибки (решает Learner; здесь определяет тяжесть).</param>
public sealed record StrokeCountConfig(int ExtraForP4, int ExtraForStop);

/// <summary>Проверка ретрейса (P6).</summary>
/// <param name="Tolerance">Расстояние до написанного, в пределах которого точка считается лежащей по написанному, x-height.</param>
/// <param name="MinShareOfReference">Доля ретрейса по написанному должна быть не меньше этой доли от эталонной.</param>
/// <param name="MinPriorPoints">Если до ретрейса написано меньше точек, проверка неприменима (написано нечего повторять).</param>
public sealed record RetraceConfig(double Tolerance, double MinShareOfReference, int MinPriorPoints);

/// <summary>Процессные пороги.</summary>
/// <param name="StartTolerance">Допуск старта (P1), x-height.</param>
/// <param name="Direction">Направление.</param>
/// <param name="StrokeCount">Число штрихов.</param>
/// <param name="Retrace">Ретрейс.</param>
public sealed record ProcessConfig(double StartTolerance, DirectionConfig Direction, StrokeCountConfig StrokeCount, RetraceConfig Retrace);

/// <summary>Единицы штрафа отложенных штрихов в подбалле <c>deferred</c> (вход функции <c>form.scores.deferred</c>).</summary>
/// <param name="Missing">Слот пуст (D1).</param>
/// <param name="Misplaced">Штрих не на месте (D2).</param>
/// <param name="Extra">Лишняя черта (D3).</param>
public sealed record DeferredPenaltyConfig(double Missing, double Misplaced, double Extra);

/// <summary>Форма (§6.4.9, §6.4.13).</summary>
/// <param name="CorridorTau">Полуширина коридора для <c>corridorShare</c> и <c>coverage</c>, x-height.</param>
/// <param name="MinLoopArea">Наименьшая площадь петли, засчитываемой как петля (F3), x-height².</param>
/// <param name="DeferredPenalty">Единицы штрафа метрики <c>deferred</c>.</param>
/// <param name="Weights">Веса метрик в <c>form</c> (ненормированные); порядок ключей — порядок в файле.</param>
/// <param name="Scores">Функции подбаллов по ключам метрик.</param>
public sealed record FormConfig(
    double CorridorTau,
    double MinLoopArea,
    DeferredPenaltyConfig DeferredPenalty,
    ImmutableArray<KeyValuePair<string, double>> Weights,
    ImmutableDictionary<string, ScoreFunction> Scores);

/// <summary>Отложенные штрихи (§6.4.8).</summary>
/// <param name="AcceptTiming">Какое время принимается.</param>
/// <param name="NearDistance">Дальше этого от ожидаемого положения — D2, x-height.</param>
/// <param name="AssignMaxDistance">Дальше этого штрих не считается этим слотом вообще (D1 и лишний штрих).</param>
/// <param name="DotMaxLength">Штрих короче этого считается точкой.</param>
/// <param name="BarMinLengthRatio">Штрих короче этой доли эталонной перекладины в слот не подходит.</param>
/// <param name="BarMaxLengthRatio">Длиннее этой доли — тоже.</param>
/// <param name="ExtraMarkMaxLength">Лишний штрих короче этого и ...</param>
/// <param name="ExtraMarkHorizontalShare">... с долей горизонтального смещения не меньше этой — лишняя черта (D3), иначе лишний штрих (P4).</param>
/// <param name="StrictTimingSeverity">Тяжесть D4 в режиме <see cref="DeferredAcceptPolicy.AfterWord"/>.</param>
public sealed record DeferredConfig(
    DeferredAcceptPolicy AcceptTiming,
    double NearDistance,
    double AssignMaxDistance,
    double DotMaxLength,
    double BarMinLengthRatio,
    double BarMaxLengthRatio,
    double ExtraMarkMaxLength,
    double ExtraMarkHorizontalShare,
    double StrictTimingSeverity);

/// <summary>Пороги находок формы и тяжесть.</summary>
/// <param name="F1DtwMean">F1: среднее отклонение больше (x-height) — общее отклонение формы.</param>
/// <param name="F2ClosedGap">F2: превышение зазора замыкания больше (x-height).</param>
/// <param name="F4ReachError">F4: ошибка достижения линии больше (x-height).</param>
/// <param name="F5BaselineError">F5: ошибка посадки на baseline больше (x-height).</param>
/// <param name="F6SlantDeviationDeg">F6: отклонение наклона от стиля больше (градусы, только режим <c>absolute</c>).</param>
/// <param name="SeverityBase">Тяжесть находки на пороге.</param>
/// <param name="SeverityFullAtMultiple">Во сколько раз превышение порога даёт тяжесть 1.</param>
/// <param name="FixedSeverity">Тяжесть находок без порога (P3, P5, D1, D3, P6, F3).</param>
public sealed record FindingsConfig(
    double F1DtwMean,
    double F2ClosedGap,
    double F4ReachError,
    double F5BaselineError,
    double F6SlantDeviationDeg,
    double SeverityBase,
    double SeverityFullAtMultiple,
    ImmutableDictionary<ErrorCode, double> FixedSeverity);

/// <summary>Пороги уверенности и «не распознано» (§6.4.7, §6.4.13).</summary>
/// <param name="UnrecognizedMeanDtw">Среднее отклонение выровненных штрихов больше (x-height) — U0.</param>
/// <param name="UnrecognizedCoverage">Покрытие эталона меньше при сопоставленных штрихах — U0.</param>
/// <param name="UnrecognizedInkShare">Доля чернил (сопоставленных и лишних основных штрихов) в коридоре эталона меньше — U0 (каракули).</param>
/// <param name="MediumMeanDtw">Среднее отклонение больше — уверенность не выше Medium.</param>
public sealed record ConfidenceConfig(double UnrecognizedMeanDtw, double UnrecognizedCoverage, double UnrecognizedInkShare, double MediumMeanDtw);

/// <summary>
/// Числа оценщика изолированной буквы (<c>content/config/scoring.json</c>, схема <c>pfl.config.scoring/1</c>).
/// Неизменяема; хеш идентифицирует снимок (ARCHITECTURE.md §6.6). Все значения в файле — инженерные стартовые, до калибровки
/// на реальных данных (§6.7).
/// </summary>
/// <param name="Version">Версия файла конфигурации.</param>
/// <param name="ConfigHash">SHA-256 канонического JSON без блока <c>meta</c>, hex в нижнем регистре.</param>
/// <param name="ResampleStep">Шаг ресэмплинга по длине дуги, x-height.</param>
/// <param name="Smoothing">Сглаживание.</param>
/// <param name="Preprocess">Предобработка.</param>
/// <param name="Normalization">Нормализация.</param>
/// <param name="Dtw">Веса DTW.</param>
/// <param name="Matching">Сопоставление штрихов.</param>
/// <param name="Profiles">Профили допусков.</param>
/// <param name="Process">Процессные пороги.</param>
/// <param name="Form">Форма.</param>
/// <param name="Deferred">Отложенные штрихи.</param>
/// <param name="Findings">Находки.</param>
/// <param name="Confidence">Уверенность.</param>
public sealed record ScoringConfig(
    string Version,
    string ConfigHash,
    double ResampleStep,
    SmoothingConfig Smoothing,
    PreprocessConfig Preprocess,
    NormalizationConfig Normalization,
    DtwConfig Dtw,
    MatchingConfig Matching,
    ImmutableDictionary<ToleranceProfile, ProfileConfig> Profiles,
    ProcessConfig Process,
    FormConfig Form,
    DeferredConfig Deferred,
    FindingsConfig Findings,
    ConfidenceConfig Confidence)
{
    /// <summary>Путь к файлу конфигурации в <see cref="PenForLang.Core.IContentSource"/>.</summary>
    public const string DefaultPath = "config/scoring.json";

    /// <summary>Множитель допуска профиля.</summary>
    /// <param name="profile">Профиль.</param>
    public double ToleranceScaleOf(ToleranceProfile profile) => Profiles[profile].ToleranceScale;
}
