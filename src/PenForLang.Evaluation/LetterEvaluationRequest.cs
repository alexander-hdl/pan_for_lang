using PenForLang.Glyphs;
using PenForLang.Ink;

namespace PenForLang.Evaluation;

/// <summary>
/// Запрос оценки изолированной буквы (ARCHITECTURE.md §6.2, §6.4.10). Неизменяем. Числа оценки (пороги, веса) берутся
/// из <see cref="Config.ScoringConfig"/>, с которой создан оценщик, а не из запроса.
/// </summary>
/// <param name="Ink">Попытка в системе линейки (<see cref="RulerProjection.Project"/>).</param>
/// <param name="Glyph">Эталон: глиф целевой буквы (с входящим и выходящим штрихами изолированной буквы).</param>
/// <param name="Style">Стиль глифа: линии линейки и режим оценки наклона (<c>slantScoring</c>).</param>
/// <param name="Tolerance">Профиль допусков формы.</param>
/// <param name="DeferredAccept">
/// Какое время отложенных штрихов принимается; <c>null</c> — значение <c>scoring.json → deferred.acceptTiming</c>
/// (настройка профиля поверх конфигурации, ARCHITECTURE.md §3.6, слой 4).
/// </param>
/// <param name="Context">Контекст буквы: изолированная (по умолчанию) или в слове (<see cref="WordEvaluator"/> оценивает вырезанный участок буквы).</param>
public sealed record LetterEvaluationRequest(
    RulerInk Ink,
    Glyph Glyph,
    StyleManifest Style,
    ToleranceProfile Tolerance = ToleranceProfile.Learning,
    DeferredAcceptPolicy? DeferredAccept = null,
    LetterContext Context = LetterContext.Isolated);
