# Стиль `zaner-bloser` (в интерфейсе — «Американская пропись» / «American cursive»)

Стиль, которому учит программа (`role = teach`, `script = cursive`). Внутренний id `zaner-bloser` встречается
только в файлах и коде; название с товарным знаком в интерфейсе не показывается (решение заказчика).

## Состав

| Файл | Что |
|---|---|
| `style.json` | линейки, наклон, правила соединений JF1–JF9, варианты, политика отложенных штрихов, настройки анимации |
| `glyphs/<символ>.<вариант>.json` | глифы: сейчас `a`, `i`, `t` (вариант `default`) |
| `labels/ru.json`, `labels/en.json` | словесные метки сегментов (из них собирается фраза буквы) |

Схемы — `schemas/pfl.style.v1.schema.json`, `pfl.glyph.v1.schema.json`, `pfl.labels.v1.schema.json`.

## Статус форм

**Иллюстративные, не сверены с прописью** (`meta.source = "illustrative"`, `meta.reviewed = false`).
Формы нарисованы по описанию ZB (undercurve, downcurve, ретрейс, отложенные точка и перекладина), наклон 20° от
вертикали вправо (`slantDeg`) и линейки (`lines`) — рабочие допущения. Сверка человеком (методист, `needs-human`):
`docs/spikes/glyphs-v0/{a,i,t}.svg`. После сверки: поправить координаты, выставить `reviewed: true` и `reviewer`.

## Проверка и просмотр

```
pfl validate content/styles/zaner-bloser
pfl render content/styles/zaner-bloser a -o artifacts/a.svg [--keypoints]
```

Если формы менялись, обновить снимки: `PFL_UPDATE_GOLDEN=1 dotnet test tests/PenForLang.Glyphs.Tests`
(перезапишет `tests/PenForLang.Glyphs.Tests/golden/*.svg` и копии для ревью `docs/spikes/glyphs-v0/*.svg`).

## Договорённости формата (`pfl.glyph/1`)

- Координаты — линейка: `u` вдоль строки, `v` вверх, baseline = 0, x-height = 1.
- `t` во всех местах — доля длины дуги штриха (не параметр кривой Безье).
- `entry.tangent` — направление подхода к точке входа (конец `leadIn`), `exit.tangent` — направление выхода (начало `leadOut`).
- `deferred[].anchor.offset` — вектор от точки привязки до середины (по длине дуги) отложенного штриха.
- Излом на стыке сегментов допустим только в точке `cusp` (ключевая точка с тем же `t`).
