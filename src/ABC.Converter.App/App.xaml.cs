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
using ABC.Converter.Infrastructure.Persistence;
using ABC.Converter.Core.Queue;

namespace ABC.Converter.App;

public partial class App : Application
{
    private readonly HttpClient comicVineHttpClient = new();
    private IQueueStore? queueStore;
    private Mutex? instanceMutex;
    private bool ownsMutex;
    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        try
        {
        instanceMutex = new Mutex(false, @"Local\ABC.Converter.Queue");
        try { ownsMutex = instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex)
        {
            MessageBox.Show("ABC Converter is already running.", "ABC Converter");
            Shutdown();
            return;
        }
        queueStore = new SqliteQueueStore(ApplicationDataPaths.QueueDatabasePath);
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
            comicVineSettings,
            queueStore: queueStore);
        await viewModel.InitializeAsync(CancellationToken.None);
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        MainWindow = window;
        window.Show();
        }
        catch (Exception)
        {
            if (queueStore is not null) await queueStore.DisposeAsync();
            queueStore = null;
            MessageBox.Show("The saved queue could not be opened. Close ABC Converter, back up abc-converter.db in Local AppData/ABC Converter, and check that the application has permission to access that folder. A newer database requires a newer app version.",
                "ABC Converter could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        if (queueStore is not null) queueStore.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        comicVineHttpClient.Dispose();
        base.OnExit(eventArgs);
    }
}
