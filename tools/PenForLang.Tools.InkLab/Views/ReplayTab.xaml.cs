using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PenForLang.Tools.InkLab.ViewModels;

namespace PenForLang.Tools.InkLab.Views;

/// <summary>Вкладка «Повтор»: список записей, воспроизведение и метаданные. Логика — в <see cref="ReplayTabViewModel"/>.</summary>
public partial class ReplayTab : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private ReplayTabViewModel? _vm;

    /// <summary>Создаёт вкладку.</summary>
    public ReplayTab()
    {
        InitializeComponent();
        foreach (double speed in ReplayTabViewModel.Speeds)
        {
            SpeedBox.Items.Add(speed.ToString("0.#", CultureInfo.InvariantCulture) + "×");
        }

        SpeedBox.SelectedIndex = 1;

        _timer.Tick += (_, _) => _vm?.Tick();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => _timer.Start();
    }

    /// <summary>Связывает вкладку с ViewModel.</summary>
    public void Initialize(ReplayTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        DataContext = vm;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка с записями (*.pfl.jsonl.gz)" };
        if (_vm is not null && dialog.ShowDialog() == true)
        {
            _vm.OpenFolder(dialog.FolderName);
        }
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Запись пера", Filter = "Записи пера (*.pfl.jsonl.gz)|*.pfl.jsonl.gz|Все файлы|*.*" };
        if (_vm is not null && dialog.ShowDialog() == true)
        {
            _vm.Load(dialog.FileName);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && _vm.Folder.Length > 0)
        {
            _vm.OpenFolder(_vm.Folder);
        }
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null && SpeedBox.SelectedIndex >= 0)
        {
            _vm.Speed = ReplayTabViewModel.Speeds[SpeedBox.SelectedIndex];
        }
    }
}
