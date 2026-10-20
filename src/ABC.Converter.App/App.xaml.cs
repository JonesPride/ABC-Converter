using System.IO;
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

        string startupStage = "initializing the application";
        try
        {
        instanceMutex = new Mutex(false, @"Global\ABC.Converter.Queue." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
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
        startupStage = "restoring the saved queue";
        await viewModel.InitializeAsync(CancellationToken.None);
        startupStage = "opening the main window";
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        MainWindow = window;
        window.Show();
        }
        catch (Exception exception)
        {
            string details = SafeExceptionDetails.Format(exception);
            string? logPath = null;
            try
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ABC Converter");
                Directory.CreateDirectory(folder);
                logPath = Path.Combine(folder, "startup-error.log");
                File.WriteAllText(logPath, $"Failure while {startupStage}.{Environment.NewLine}{details}");
            }
            catch (Exception)
            {
                // A diagnostic write failure must not hide the original startup error.
                logPath = null;
            }
            if (queueStore is not null)
            {
                try { await queueStore.DisposeAsync(); }
                catch (Exception) { /* Preserve the original startup failure. */ }
                queueStore = null;
            }
            Exception cause = exception.GetBaseException();
            string message = $"ABC Converter failed while {startupStage}.{Environment.NewLine}{Environment.NewLine}" +
                SafeExceptionDetails.Redact($"{cause.GetType().Name}: {cause.Message}");
            if (logPath is not null)
                message += $"{Environment.NewLine}{Environment.NewLine}Diagnostic details: {logPath}";
            message += $"{Environment.NewLine}{Environment.NewLine}Keep the saved queue database intact.";
            MessageBox.Show(message, "ABC Converter could not start", MessageBoxButton.OK, MessageBoxImage.Error);
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
