using System.Net.Http;
using System.Windows;
using ABC.Converter.App.Services;
using ABC.Converter.App.ViewModels;
using ABC.Converter.Epub;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Infrastructure.ComicVine;
using ABC.Converter.Infrastructure.Conversion;
using ABC.Converter.Infrastructure.Files;
using ABC.Converter.Infrastructure.Metadata;

namespace ABC.Converter.App;

public partial class App : Application
{
    private readonly HttpClient comicVineHttpClient = new();
    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        var archiveReader = new CbzArchiveReader();
        var epubBuilder = new FixedLayoutEpubBuilder(archiveReader);
        var validator = new EpubStructureValidator();
        var outputPathPolicy = new SeriesOutputPathPolicy();
        var apiKeyStore = new WindowsCredentialComicVineApiKeyStore();
        var comicVineSettings = new ComicVineSettingsViewModel(apiKeyStore);
        var comicVineLookup = new ComicVineIssueLookup(
            apiKeyStore,
            comicVineHttpClient,
            "ABC-Converter/0.2 (https://github.com/JonesPride/ABC-Converter)");
        var metadataResolver = new ComicVineMetadataResolver(
            new ComicInfoMetadataResolver(),
            comicVineLookup,
            () => comicVineSettings.IsConfigured);
        var conversionService = new ComicConversionService(
            archiveReader,
            epubBuilder,
            validator,
            outputPathPolicy,
            metadataResolver: metadataResolver);
        var viewModel = new MainWindowViewModel(
            conversionService,
            new FolderPicker(),
            Dispatcher,
            new CommandErrorHandler().Handle,
            comicVineSettings);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        comicVineHttpClient.Dispose();
        base.OnExit(eventArgs);
    }
}
