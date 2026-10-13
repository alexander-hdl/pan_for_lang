namespace PenForLang.Evaluation;

/// <summary>
/// Версия оценщика. Записывается в каждую попытку, чтобы результаты разных версий не смешивались
/// (ARCHITECTURE.md §1.4, «Детерминизм оценщика»).
/// </summary>
public static class EvaluatorVersion
{
    /// <summary>Текущая версия алгоритма (меняется при любом изменении результатов).</summary>
    public const string Current = "v0.0";
}
