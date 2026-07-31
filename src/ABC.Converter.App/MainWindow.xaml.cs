using System.IO;
using System.Windows;
using System.Windows.Controls;
using ABC.Converter.App.ViewModels;

namespace ABC.Converter.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Effects = TryGetDroppedPaths(
            eventArgs.Data,
            out string[] paths) &&
            paths.Any(CanContainCbzInput)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (DataContext is not MainWindowViewModel viewModel ||
            !TryGetDroppedPaths(eventArgs.Data, out string[] paths) ||
            !paths.Any(CanContainCbzInput))
        {
            return;
        }

        try
        {
            await viewModel.AddPathsAsync(paths);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Could not add comics",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnComicVineApiKeyChanged(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (DataContext is MainWindowViewModel viewModel &&
            viewModel.ComicVineSettings is not null &&
            sender is PasswordBox passwordBox)
        {
            viewModel.ComicVineSettings.ApiKey = passwordBox.Password;
        }
    }

    private void OnComicVineSaveClicked(
        object sender,
        RoutedEventArgs eventArgs)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is MainWindowViewModel viewModel &&
                viewModel.ComicVineSettings?.IsConfigured == true)
            {
                ComicVineApiKeyBox.Clear();
            }
        });
    }

    private static bool TryGetDroppedPaths(
        IDataObject data,
        out string[] paths)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) &&
            data.GetData(DataFormats.FileDrop) is string[] droppedPaths)
        {
            paths = droppedPaths;
            return true;
        }

        paths = [];
        return false;
    }

    private static bool CanContainCbzInput(string path)
    {
        return Directory.Exists(path) ||
            File.Exists(path) &&
            string.Equals(
                Path.GetExtension(path),
                ".cbz",
                StringComparison.OrdinalIgnoreCase);
    }
}
