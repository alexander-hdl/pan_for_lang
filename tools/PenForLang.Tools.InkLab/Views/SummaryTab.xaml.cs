using System.Windows.Controls;
using PenForLang.Tools.InkLab.ViewModels;

namespace PenForLang.Tools.InkLab.Views;

/// <summary>Вкладка «Сводка»: сколько записей по видам и сколько размечено. Логика — в <see cref="SummaryTabViewModel"/>.</summary>
public partial class SummaryTab : UserControl
{
    /// <summary>Создаёт вкладку.</summary>
    public SummaryTab()
    {
        InitializeComponent();
    }

    /// <summary>Связывает вкладку с ViewModel.</summary>
    public void Initialize(SummaryTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        DataContext = vm;
    }
}
