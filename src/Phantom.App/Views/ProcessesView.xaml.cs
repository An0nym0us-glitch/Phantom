using System.Windows.Controls;
using Phantom.App.ViewModels;

namespace Phantom.App.Views;

public partial class ProcessesView : UserControl
{
    public ProcessesView() => InitializeComponent();

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        // The grid applies the sort itself; live re-sorting additionally needs to know which
        // property to watch so rows keep their order as values change.
        if (DataContext is ProcessesViewModel vm)
        {
            vm.SetLiveSortProperty(e.Column.SortMemberPath);
        }
    }
}
