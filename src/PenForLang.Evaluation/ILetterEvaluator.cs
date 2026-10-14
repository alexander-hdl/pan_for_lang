namespace PenForLang.Evaluation;

/// <summary>
/// Оценщик изолированной буквы v0 (ARCHITECTURE.md §6.4.10). Чистая функция от (запрос, снимок конфигурации, с которым
/// создан оценщик): одинаковые входы дают побитово одинаковый результат в одном процессе и на одной машине; между машинами
/// совпадают решения и коды, числа — с допуском (§6.6, A19). Реализации потокобезопасны.
/// </summary>
public interface ILetterEvaluator
{
    /// <summary>Версия оценщика и хеш конфигурации.</summary>
    EvaluatorInfo Info { get; }

    /// <summary>
    /// Оценивает попытку написать изолированную букву: предобработка, нормализация к линейкам, сопоставление штрихов,
    /// старт/направление/порядок/число штрихов, отложенные штрихи, форма. Контексты слова (<see cref="LetterContext.WordFirst"/>,
    /// <see cref="LetterContext.WordInner"/>) — для подвыборки чернил одной буквы слова (<see cref="WordEvaluator"/>).
    /// </summary>
    /// <param name="request">Запрос.</param>
    /// <exception cref="ArgumentNullException">Запрос или его части не заданы.</exception>
    EvaluationResult Evaluate(LetterEvaluationRequest request);
}
