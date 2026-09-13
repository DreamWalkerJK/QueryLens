using Avalonia.Controls;
using Avalonia.Platform.Storage;
using QueryLens.Desktop.ViewModels;

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

    private async void PickQueryImportFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = await PickFileAsync("选择慢查询日志或 JSON 快照", ["*.log", "*.txt", "*.json"]);
        if (selected is not null && DataContext is MainViewModel vm)
            vm.QueryImportPath = selected;
    }

    private async void PickPlanImportFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = await PickFileAsync("选择 JSON 或 XML 执行计划", ["*.json", "*.xml"]);
        if (selected is not null && DataContext is MainViewModel vm)
            vm.PlanImportPath = selected;
    }

    private async void PickReportExportFile(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "选择脱敏报告保存位置",
            SuggestedFileName = "QueryLens-report.json",
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("JSON 文件") { Patterns = ["*.json"] }]
        });
        var path = selected?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainViewModel vm)
            vm.ReportExportPath = path;
    }

    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("支持的文件") { Patterns = patterns }]
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
