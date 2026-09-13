using Avalonia.Controls;

namespace QueryLens.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is QueryLens.Desktop.ViewModels.MainViewModel viewModel)
                await viewModel.InitializeAsync();
        };
    }
}
