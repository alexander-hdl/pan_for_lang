using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using PenForLang.Tools.InkLab.Corpus;
using PenForLang.Tools.InkLab.Mvvm;

namespace PenForLang.Tools.InkLab.ViewModels;

/// <summary>Вкладка «Сводка»: сколько записей по видам и субъектам, сколько размечено; сохранение в <c>corpus-summary.md</c>.</summary>
public sealed class SummaryTabViewModel : ViewModelBase
{
    /// <summary>Имя файла сводки в папке корпуса.</summary>
    public const string SummaryFileName = "corpus-summary.md";

    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _saveCommand;
    private string _folder;
    private string _status = "Нажмите «Обновить».";
    private string _text = string.Empty;
    private CorpusSummary? _summary;

    /// <summary>Создаёт вкладку.</summary>
    /// <param name="folder">Папка корпуса.</param>
    public SummaryTabViewModel(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = folder;
        _refreshCommand = new RelayCommand(Refresh);
        _saveCommand = new RelayCommand(() => Save(), () => _summary is not null);
    }

    /// <summary>Строки по видам записей.</summary>
    public ObservableCollection<SummaryRow> ByGroup { get; } = [];

    /// <summary>Строки по субъектам.</summary>
    public ObservableCollection<SummaryRow> BySubject { get; } = [];

    /// <summary>Команда «Обновить».</summary>
    public RelayCommand RefreshCommand => _refreshCommand;

    /// <summary>Команда «Сохранить сводку».</summary>
    public RelayCommand SaveCommand => _saveCommand;

    /// <summary>Папка корпуса.</summary>
    public string Folder
    {
        get => _folder;
        set => Set(ref _folder, value ?? string.Empty);
    }

    /// <summary>Сообщение.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Сводка в виде Markdown.</summary>
    public string Text
    {
        get => _text;
        private set => Set(ref _text, value);
    }

    /// <summary>Последняя построенная сводка.</summary>
    public CorpusSummary? Summary => _summary;

    /// <summary>Пересчитывает сводку по папке.</summary>
    public void Refresh()
    {
        if (!Directory.Exists(_folder))
        {
            Status = "Папка не найдена: " + _folder;
            return;
        }

        _summary = CorpusSummary.Build(CorpusIndex.Scan(_folder));
        ByGroup.Clear();
        foreach (SummaryRow row in _summary.ByGroup)
        {
            ByGroup.Add(row);
        }

        BySubject.Clear();
        foreach (SummaryRow row in _summary.BySubject)
        {
            BySubject.Add(row);
        }

        Text = _summary.ToMarkdown(_folder);
        Status = $"Записей: {_summary.Total}, размечено верно: {_summary.Overall.Valid + _summary.Overall.Rejected}.";
        Raise(nameof(Summary));
        _saveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Сохраняет сводку в <see cref="SummaryFileName"/> корня корпуса.</summary>
    /// <returns>Путь файла или <c>null</c>, если записать не удалось.</returns>
    public string? Save()
    {
        if (_summary is null)
        {
            return null;
        }

        string path = Path.Combine(_folder, SummaryFileName);
        try
        {
            File.WriteAllText(path, Text, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "Не удалось сохранить сводку: " + ex.Message;
            return null;
        }

        Status = "Сводка сохранена: " + path;
        return path;
    }
}
