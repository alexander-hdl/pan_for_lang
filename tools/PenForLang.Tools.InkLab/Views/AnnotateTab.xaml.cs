using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PenForLang.Tools.InkLab.ViewModels;

namespace PenForLang.Tools.InkLab.Views;

/// <summary>Вкладка «Разметка»: след записи, список записей и редактор отложенных штрихов и стыков. Логика — в <see cref="AnnotateTabViewModel"/>.</summary>
public partial class AnnotateTab : UserControl
{
    private AnnotateTabViewModel? _vm;
    private bool _updatingLetters;

    /// <summary>Создаёт вкладку.</summary>
    public AnnotateTab()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Связывает вкладку с ViewModel.</summary>
    public void Initialize(AnnotateTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        DataContext = vm;
        Strokes.RulerClicked += vm.HandleClick;
        vm.PropertyChanged += OnViewModelChanged;
        RefreshLetters();
    }

    /// <summary>Стрелки ← и → сдвигают последнюю поставленную точку стыка на одну точку записи (когда фокус не в поле ввода).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || e.OriginalSource is TextBox || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            return;
        }

        if (e.Key is Key.Left or Key.Right)
        {
            _vm.Nudge(e.Key == Key.Left ? -1 : 1);
            e.Handled = true;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AnnotateTabViewModel.Text))
        {
            RefreshLetters();
        }
        else if (e.PropertyName is nameof(AnnotateTabViewModel.Tool))
        {
            bool deferred = _vm?.Tool == AnnotateTool.Deferred;
            DeferredTool.IsChecked = deferred;
            BoundaryTool.IsChecked = !deferred;
        }
    }

    private void RefreshLetters()
    {
        if (_vm is null)
        {
            return;
        }

        _updatingLetters = true;
        try
        {
            LetterBox.Items.Clear();
            for (int i = 0; i < _vm.Text.Length; i++)
            {
                LetterBox.Items.Add(_vm.Text[i].ToString());
            }

            LetterBox.SelectedIndex = _vm.Text.Length == 0 ? -1 : Math.Min(_vm.SelectedLetter, _vm.Text.Length - 1);
        }
        finally
        {
            _updatingLetters = false;
        }
    }

    private void OnLetterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is not null && !_updatingLetters && LetterBox.SelectedIndex >= 0)
        {
            _vm.SelectedLetter = LetterBox.SelectedIndex;
        }
    }

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && BoundaryTool is not null && DeferredTool is not null)
        {
            _vm.Tool = DeferredTool.IsChecked == true ? AnnotateTool.Deferred : AnnotateTool.Boundary;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка с записями (*.pfl.jsonl.gz)" };
        if (_vm is not null && dialog.ShowDialog() == true)
        {
            _vm.OpenFolder(dialog.FolderName);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _vm?.Refresh();
}
