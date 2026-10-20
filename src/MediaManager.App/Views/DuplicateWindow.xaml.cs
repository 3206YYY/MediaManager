using System.Windows;
using MediaManager.App.ViewModels;

namespace MediaManager.App.Views;

/// <summary>
/// Interaction logic for DuplicateWindow.xaml
/// </summary>
public partial class DuplicateWindow : Window
{
    private readonly DuplicateViewModel _viewModel;

    public DuplicateWindow(DuplicateViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void OnItemChecked(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshSummary();
    }
}
