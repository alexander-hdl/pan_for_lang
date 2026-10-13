# CLAUDE.md — правила для агентов-кодеров pan_for_lang

Программа учит ребёнка писать английской прописью на перьевом дисплее. WPF, .NET 10, офлайн.
Целевая ОС: Windows 10 22H2 (основная), Windows 11 позже (ARCHITECTURE.md v1.2, ADR-001).

Документы: [docs/METHODOLOGY.md](docs/METHODOLOGY.md) (что делаем и зачем),
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (как; план задач — §12). `docs/` не правь без поручения.

## Структура решения (`PenForLang.sln`)
- `src/PenForLang.*` — продуктовые сборки. Сейчас (веха v0): `Core`, `Ink`, `Glyphs`, `Evaluation`,
  `Platform.Windows`, `Wpf.Controls`, `App.Wpf`. Остальные из §2.2 появятся по плану.
- `tools/` — `PenForLang.Testing` (FakeClock, позже SynthInk), `PenForLang.Tools.Cli` (исполняемый файл `pfl`).
- `tests/PenForLang.<Модуль>.Tests` — по тестовому проекту на сборку; `Architecture.Tests` — правила R1–R5;
  `Architecture.Fixtures` — намеренно нарушающая сборка для самопроверки правил (не продукт).
- `content/`, `schemas/`, `build/` — данные, JSON-схемы, скрипты (по мере появления задач).

## Команды (из корня репозитория, SDK 10.0.401 по `global.json`)
- Сборка как в CI: `dotnet build -c Release -warnaserror`
- Тесты: `dotnet test` (Microsoft.Testing.Platform, xUnit v3).
  Фильтр: `--filter-not-trait "Category=Interactive"`; категории: UI, Interactive, Hardware, Nightly.
- Формат: `dotnet format --verify-no-changes`
- `pfl`: `dotnet run --project tools/PenForLang.Tools.Cli -- --version`
- Перед коммитом: сборка с `-warnaserror`, все тесты и формат зелёные.

## Правила зависимостей (проверяет `Architecture.Tests`)
- R1: сборки `net10.0` не ссылаются на `net10.0-windows`, WPF, WinForms, Sqlite, NAudio, PDFsharp
  (Sqlite и PDFsharp — только в `Infrastructure`).
- R3: в `src/` нет сети (`System.Net.Http`, `Sockets`, `WebRequest`).
- R4: P/Invoke только в `Platform.Windows`. WPF/XAML только в `Wpf.Controls` и `App.Wpf`.
- R5: ссылки между проектами — только по графу в `tests/PenForLang.Architecture.Tests/AllowedGraph.cs`
  (сверен с §2.3). Новый проект или ссылка = осознанная правка этого файла в том же коммите.
- R2 (BannedApiAnalyzers): домен (`Core`, `Ink`, `Glyphs`, `Evaluation`, потом `Curriculum`, `Learner`, `Lesson`,
  `Reporting`) не использует `DateTime.Now/UtcNow`, `Environment.TickCount*`, `Stopwatch`, `Random`, `Guid.NewGuid`,
  `Parallel.*`, `Task.Run`, `File.*`, `Directory.*`, `Console.*`, сеть. Список — `BannedSymbols.Domain.txt`
  (в `Core` и `Evaluation` ещё `BannedSymbols.Numeric.txt`: нет SIMD-интринсиков).
  Новый доменный проект: `<IsDomainProject>true</IsDomainProject>` в csproj.
  Не отключай анализатор и не вноси исключений — вынеси код в корень композиции или в `Platform.Windows`.

## Принципы кода
- Логика — в чистых библиотеках `net10.0` без WPF/Win32/SQLite. Тонкие края: `Platform.Windows`, `App.Wpf`.
- Детерминизм: время только через `IClock`, случайность через `IRandomSource` (`Pcg32`); в тестах `FakeClock`.
- Числа методики и оценщика — в `content/config` (JSON), не в коде. Пользовательских строк в домене нет — только ключи.
- Тесты обязательны: новый код = минимум один тест; багфикс = тест, который падал.
  Тест, доказывающий, что проверка ловит нарушение, важнее теста, что нарушений нет.
- Стиль: `.editorconfig` (file-scoped namespaces, `_camelCase` для приватных полей), Nullable, предупреждения = ошибки.
  Идентификаторы, имена типов и код — на английском; комментарии и документы — на русском.
- Пакеты — только через `Directory.Packages.props` (Central Package Management); лицензия MIT/Apache/BSD.
  Не добавляй пакет без нужды и без записи лицензии рядом с версией.
- Публичные типы контрактов документируются (`///`); контракты v1 меняет только оркестратор.

## Приватность при проверке UI
- Никогда не делай снимок всего экрана или рабочего стола: на нём личные данные владельца машины.
  Снимай только окно своего процесса (по HWND/PrintWindow/RenderTargetBitmap) или рендер в файл.
- Не открывай и не читай чужие окна, браузер, файлы вне репозитория и временных папок задачи.
- Если личные данные всё же попали в файл — удали его сразу и сообщи оркестратору.

## Git
- Одна задача плана (V0.x, M1.x) = одна ветка (`v0/<кратко>`) = один проверяемый результат. Не пушь и не мержи сам.
- Сообщение коммита начинается с ID задачи: `V0.1: скелет решения и CI`.
- В конце сообщения: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`
- Добавляй файлы явными путями (не `git add -A`): в дереве могут лежать чужие неотслеженные файлы.
- Концы строк — LF (`.gitattributes`: `* text=auto eol=lf`; `.sln`, `.bat`, `.cmd` — CRLF). Бинарные файлы помечены.
- Golden-снимки оценщика принимает только оркестратор или человек. Работу человека/железа помечай
  `needs-human` / `needs-hardware`.
