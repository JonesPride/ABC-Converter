using System.Windows;
using ABC.Converter.App.Services;
using ABC.Converter.App.ViewModels;
using ABC.Converter.Epub;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Infrastructure.Conversion;
using ABC.Converter.Infrastructure.Files;

namespace ABC.Converter.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        var archiveReader = new CbzArchiveReader();
        var epubBuilder = new FixedLayoutEpubBuilder(archiveReader);
        var validator = new EpubStructureValidator();
        var outputPathPolicy = new SeriesOutputPathPolicy();
        var conversionService = new ComicConversionService(
            archiveReader,
            epubBuilder,
            validator,
            outputPathPolicy);
        var viewModel = new MainWindowViewModel(
            conversionService,
            new FolderPicker(),
            Dispatcher);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        MainWindow = window;
        window.Show();
    }
}
