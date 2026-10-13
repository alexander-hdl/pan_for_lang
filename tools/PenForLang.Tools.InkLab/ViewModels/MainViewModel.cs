using PenForLang.Core;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Mvvm;
using PenForLang.Tools.InkLab.Scene;

namespace PenForLang.Tools.InkLab.ViewModels;

/// <summary>Корневая ViewModel окна InkLab: вкладки «Запись», «Повтор», «Разметка», «Сводка» и общий корень корпуса.</summary>
public sealed class MainViewModel : ViewModelBase
{
    /// <summary>Номера вкладок в <c>TabControl</c>.</summary>
    public static class Tabs
    {
        /// <summary>Запись.</summary>
        public const int Record = 0;

        /// <summary>Повтор.</summary>
        public const int Replay = 1;

        /// <summary>Разметка.</summary>
        public const int Annotate = 2;

        /// <summary>Сводка.</summary>
        public const int Summary = 3;
    }

    private int _selectedTab;

    /// <summary>Создаёт корневую ViewModel.</summary>
    /// <param name="options">Параметры командной строки.</param>
    /// <param name="monotonicClock">Монотонные часы источника пера.</param>
    /// <param name="clock">Календарные часы (воспроизведение, имена файлов, отметка времени разметки).</param>
    /// <param name="guides">Линии линейки стиля для рисования.</param>
    public MainViewModel(LabOptions options, IMonotonicClock monotonicClock, IClock clock, SceneGuides? guides)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(monotonicClock);
        ArgumentNullException.ThrowIfNull(clock);
        CorpusRoot = options.CorpusDirectory is { Length: > 0 } dir ? System.IO.Path.GetFullPath(dir) : CorpusStore.DefaultRoot();

        Record = new RecordTabViewModel(CorpusRoot, monotonicClock, () => clock.UtcNow.ToLocalTime());
        if (options.Subject is { Length: > 0 } subject)
        {
            Record.SubjectId = subject;
        }

        Record.RefreshKnownSubjects();
        Replay = new ReplayTabViewModel(clock, guides);
        Annotate = new AnnotateTabViewModel(guides, () => clock.UtcNow, options.Annotator);
        Summary = new SummaryTabViewModel(CorpusRoot);
        Record.IsInputEnabled = true;
    }

    /// <summary>Корень корпуса (общий для вкладок).</summary>
    public string CorpusRoot { get; }

    /// <summary>Вкладка «Запись».</summary>
    public RecordTabViewModel Record { get; }

    /// <summary>Вкладка «Повтор».</summary>
    public ReplayTabViewModel Replay { get; }

    /// <summary>Вкладка «Разметка».</summary>
    public AnnotateTabViewModel Annotate { get; }

    /// <summary>Вкладка «Сводка».</summary>
    public SummaryTabViewModel Summary { get; }

    /// <summary>Номер выбранной вкладки; перо принимает только «Запись».</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value))
            {
                return;
            }

            Record.IsInputEnabled = value == Tabs.Record;
            switch (value)
            {
                case Tabs.Replay when Replay.Folder.Length == 0 && System.IO.Directory.Exists(CorpusRoot):
                    Replay.OpenFolder(CorpusRoot);
                    break;
                case Tabs.Annotate when Annotate.Folder.Length == 0 && System.IO.Directory.Exists(CorpusRoot):
                    Annotate.OpenFolder(CorpusRoot);
                    break;
                case Tabs.Summary when System.IO.Directory.Exists(CorpusRoot):
                    Summary.Refresh();
                    break;
                default:
                    break;
            }
        }
    }
}
