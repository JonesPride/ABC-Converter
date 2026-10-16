using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ABC.Converter.App.Commands;
using ABC.Converter.App.Services;
using ABC.Converter.Core.Models;
using ABC.Converter.Core.Queue;
using ABC.Converter.Infrastructure.Persistence;
using ABC.Converter.Infrastructure.Conversion;
using ABC.Converter.Infrastructure.Metadata;

namespace ABC.Converter.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ComicConversionService _conversionService;
    private readonly FolderPicker _folderPicker;
    private readonly Dispatcher? _dispatcher;
    private readonly HashSet<string> _knownSourcePaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _activeJobLock = new();
    private readonly AsyncCommand _addFilesCommand;
    private readonly AsyncCommand _addFolderCommand;
    private readonly AsyncCommand _startCommand;
    private readonly AsyncCommand _cancelSelectedCommand;
    private readonly AsyncCommand _browseOutputCommand;
    private readonly AsyncCommand _retrySelectedCommand;
    private readonly AsyncCommand _retryAllFailedCommand;
    private readonly AsyncCommand _saveComicVineSettingsCommand;
    private readonly AsyncCommand _deleteComicVineSettingsCommand;
    private readonly AsyncCommand _approveMetadataCommand;
    private string _outputRoot;
    private ConversionJobViewModel? _selectedJob;
    private ConversionJobViewModel? _activeJob;
    private CancellationTokenSource? _activeJobCancellation;
    private int _isProcessing;
    private readonly IQueueStore? _queueStore;
    private readonly ISourceFingerprintProvider _fingerprints;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _addGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _processingLock = new();
    private Task? _processingTask;
    private bool _stopping;
    private bool _persistenceFailed;
    private int _initializationStarted;
    private bool _ready;
    private int _pendingMutations;
    private TaskCompletionSource _mutationsDrained = CompletedSignal();
    private Task _settingsWrite = Task.CompletedTask;
    private readonly Action<Exception> _commandError;

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    public MainWindowViewModel(
        ComicConversionService conversionService,
        FolderPicker? folderPicker = null,
        Dispatcher? dispatcher = null,
        Action<Exception>? commandExceptionHandler = null,
        ComicVineSettingsViewModel? comicVineSettings = null,
        IQueueStore? queueStore = null,
        ISourceFingerprintProvider? fingerprintProvider = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _queueStore = queueStore;
        _fingerprints = fingerprintProvider ?? new SourceFingerprintProvider();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _conversionService = conversionService;
        _folderPicker = folderPicker ?? new FolderPicker();
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher;
        ComicVineSettings = comicVineSettings;
        Action<Exception> onCommandException =
            commandExceptionHandler ?? ReportCommandException;
        _commandError = onCommandException;
        _outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ABC Converter");

        _addFilesCommand = new AsyncCommand(
            AddFilesFromPickerAsync,
            onException: onCommandException);
        _addFolderCommand = new AsyncCommand(
            AddFolderFromPickerAsync,
            onException: onCommandException);
        _startCommand = new AsyncCommand(
            () => StartAsync(CancellationToken.None),
            CanStart,
            onCommandException);
        _cancelSelectedCommand = new AsyncCommand(
            CancelSelectedAsync,
            CanCancelSelected,
            onCommandException);
        _browseOutputCommand = new AsyncCommand(
            BrowseOutputAsync,
            onException: onCommandException);
        _retrySelectedCommand = new AsyncCommand(
            RetrySelectedAsync,
            CanRetrySelected,
            onCommandException);
        _retryAllFailedCommand = new AsyncCommand(
            RetryAllFailedAsync,
            () => !_stopping && !_persistenceFailed && Volatile.Read(ref _isProcessing) == 0 && Jobs.Any(job => job.Status == "Failed"),
            onCommandException);
        _saveComicVineSettingsCommand = new AsyncCommand(
            SaveComicVineSettingsAsync,
            () => ComicVineSettings is not null &&
                !string.IsNullOrWhiteSpace(ComicVineSettings.ApiKey),
            onCommandException);
        _deleteComicVineSettingsCommand = new AsyncCommand(
            DeleteComicVineSettingsAsync,
            () => ComicVineSettings?.IsConfigured == true,
            onCommandException);
        _approveMetadataCommand = new AsyncCommand(
            ApproveMetadataAsync,
            () => !_stopping && !_persistenceFailed && Volatile.Read(ref _isProcessing) == 0 && SelectedJob?.MetadataReview is not null,
            onCommandException);
        if (ComicVineSettings is not null)
        {
            ComicVineSettings.PropertyChanged += (_, _) =>
            {
                _saveComicVineSettingsCommand.RaiseCanExecuteChanged();
                _deleteComicVineSettingsCommand.RaiseCanExecuteChanged();
            };
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ConversionJobViewModel> Jobs { get; } = [];

    public ComicVineSettingsViewModel? ComicVineSettings { get; }

    public string OutputRoot
    {
        get => _outputRoot;
        set
        {
            if (_stopping) return;
            if (SetField(ref _outputRoot, value))
            {
                if (_ready && _queueStore is not null)
                    _settingsWrite = SaveOutputSettingAsync(_settingsWrite, value);
                _startCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ConversionJobViewModel? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (SetField(ref _selectedJob, value))
            {
                _cancelSelectedCommand.RaiseCanExecuteChanged();
                _retrySelectedCommand.RaiseCanExecuteChanged();
                _approveMetadataCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int QueuedCount =>
        Jobs.Count(job => job.Status == "Queued");

    public int CompletedCount =>
        Jobs.Count(job => job.Status == "Completed");

    public int NeedsReviewCount =>
        Jobs.Count(job => job.Status == "Needs Review");

    public int FailedCount =>
        Jobs.Count(job => job.Status == "Failed");

    public double OverallProgress =>
        Jobs.Count == 0
            ? 0
            : Jobs.Average(job => job.Progress);

    public ICommand AddFilesCommand => _addFilesCommand;

    public ICommand AddFolderCommand => _addFolderCommand;

    public ICommand StartCommand => _startCommand;

    public ICommand CancelSelectedCommand => _cancelSelectedCommand;

    public ICommand BrowseOutputCommand => _browseOutputCommand;

    public ICommand RetrySelectedCommand => _retrySelectedCommand;

    public ICommand RetryAllFailedCommand => _retryAllFailedCommand;

    public ICommand SaveComicVineSettingsCommand => _saveComicVineSettingsCommand;

    public ICommand DeleteComicVineSettingsCommand => _deleteComicVineSettingsCommand;

    public ICommand ApproveMetadataCommand => _approveMetadataCommand;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _initializationStarted, 1) != 0)
            throw new InvalidOperationException("The queue has already been initialized.");
        if (_queueStore is null) return;
        await _queueStore.InitializeAsync(cancellationToken);
        string? outputRoot = await _queueStore.LoadSettingAsync("OutputRoot", cancellationToken);
        var records = await _queueStore.LoadJobsAsync(cancellationToken);
        var recovered = new List<QueueJobRecord>();
        foreach (var record in records.OrderBy(j => j.CreatedUtc).ThenBy(j => j.Id))
        {
            var job = QueueRecoveryPolicy.Recover(record, path => PublishedOutputIdentity.Matches(record, path), _utcNow());
            if (!ReferenceEquals(record, job)) await _queueStore.SaveJobAsync(job, cancellationToken);
            recovered.Add(job);
        }
        await RunOnDispatcherAsync(() =>
        {
            if (outputRoot is not null) OutputRoot = outputRoot;
            foreach (var record in recovered)
            {
                var job = ConversionJobViewModel.FromRecord(record);
                job.PropertyChanged += OnJobPropertyChanged;
                Jobs.Add(job);
                _knownSourcePaths.Add(Identity(record.SourcePath, record.SourceFingerprint));
            }
            RaiseQueueStateChanged();
        });
        _ready = true;
        _settingsWrite = SaveOutputSettingAsync(_settingsWrite, OutputRoot);
        await _settingsWrite;
    }

    private async Task SaveOutputSettingAsync(Task previous, string value)
    {
        try
        {
            await previous;
            await _queueStore!.SaveSettingAsync("OutputRoot", value, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _persistenceFailed = true;
            _commandError(exception);
            throw;
        }
        finally { await RunOnDispatcherAsync(RaiseQueueStateChanged); }
    }

    private async Task ChangeJobAsync(ConversionJobViewModel job, Action change)
    {
        lock (_processingLock)
        {
            if (_stopping || _persistenceFailed || Volatile.Read(ref _isProcessing) != 0)
                throw new InvalidOperationException("Wait for queue processing and saving to finish before editing a job.");
            if (_pendingMutations++ == 0)
                _mutationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            await RunOnDispatcherAsync(change);
            await PersistJobAsync(job);
        }
        finally
        {
            lock (_processingLock)
                if (--_pendingMutations == 0) _mutationsDrained.TrySetResult();
            await RunOnDispatcherAsync(RaiseQueueStateChanged);
        }
    }

    private static string Identity(string path, string fingerprint) => path + "\0" + fingerprint;

    private async Task PersistJobAsync(ConversionJobViewModel job, QueueJobState? durableState = null, string? outputPath = null, string? outputHash = null)
    {
        if (_queueStore is null) return;
        QueueJobRecord? record = null;
        await RunOnDispatcherAsync(() => record = job.ToRecord(OutputRoot, _utcNow()));
        if (durableState is { } state) record = record! with { State = state, OutputPath = outputPath, Progress = 100,
            ErrorStage = "Publishing", TechnicalError = "sha256:" + outputHash };
        try { await _queueStore.SaveJobAsync(record!, CancellationToken.None); }
        catch { _persistenceFailed = true; throw; }
    }

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        await _addGate.WaitAsync();
        try
        {
            if (_stopping || _persistenceFailed) throw new InvalidOperationException("The queue is not accepting new work.");
            await _settingsWrite;
            string[] requestedPaths = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            string[] discoveredPaths = await Task.Run(() => DiscoverComicPaths(requestedPaths));
            var jobs = new List<ConversionJobViewModel>();
            foreach (string path in discoveredPaths)
            {
                string fingerprint = await Task.Run(() => _fingerprints.GetFingerprintAsync(path, _shutdown.Token), _shutdown.Token);
                if (_knownSourcePaths.Contains(Identity(path, fingerprint))) continue;
                string title = Path.GetFileNameWithoutExtension(path);
                jobs.Add(new ConversionJobViewModel { SourcePath = path, Series = title,
                    JobOutputRoot = OutputRoot, SourceFingerprint = fingerprint, CreatedUtc = _utcNow(), UpdatedUtc = _utcNow() });
            }
            IReadOnlyList<Guid> inserted = _queueStore is null ? jobs.Select(j => j.Id).ToArray() :
                await _queueStore.AddJobsAsync(jobs.Select(j => j.ToRecord(OutputRoot, _utcNow())).ToArray(), _shutdown.Token);
            var ids = inserted.ToHashSet();
            await RunOnDispatcherAsync(() =>
            {
                foreach (var job in jobs.Where(j => ids.Contains(j.Id)))
                {
                    if (!_knownSourcePaths.Add(Identity(job.SourcePath, job.SourceFingerprint))) continue;
                    job.PropertyChanged += OnJobPropertyChanged;
                    Jobs.Add(job);
                }
                RaiseQueueStateChanged();
            });
        }
        finally { _addGate.Release(); }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_processingLock)
        {
            if (_stopping || _persistenceFailed) return Task.CompletedTask;
            if (_processingTask is { IsCompleted: false }) return _processingTask;
            if (_pendingMutations != 0 || !_settingsWrite.IsCompletedSuccessfully) return Task.CompletedTask;
            return _processingTask = ProcessQueueAsync(cancellationToken);
        }
    }

    public async Task PrepareForShutdownAsync(TimeSpan timeout)
    {
        Task? processing;
        Task mutations;
        Task settings;
        lock (_processingLock)
        {
            _stopping = true;
            processing = _processingTask;
            mutations = _mutationsDrained.Task;
            settings = _settingsWrite;
        }
        string outputRoot = OutputRoot;
        _shutdown.Cancel();
        using var deadline = new CancellationTokenSource(timeout);
        // Run even synchronous SQLite work off the dispatcher, and bound the whole operation.
        Task finishing = Task.Run(async () =>
        {
            await Task.WhenAll(processing ?? Task.CompletedTask, mutations, settings);
            await _addGate.WaitAsync(deadline.Token);
            try
            {
                if (_queueStore is not null)
                    await _queueStore.SaveSettingAsync("OutputRoot", outputRoot, deadline.Token);
            }
            finally { _addGate.Release(); }
        });
        try { await finishing.WaitAsync(timeout); }
        catch (TimeoutException)
        {
            _ = finishing.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            ConversionJobViewModel[] queuedJobs = [];
            string outputRoot = string.Empty;
            await RunOnDispatcherAsync(() =>
            {
                queuedJobs = Jobs
                    .Where(job => job.Status == "Queued")
                    .ToArray();
                outputRoot = OutputRoot;
                RaiseQueueStateChanged();
            });

            foreach (ConversionJobViewModel job in queuedJobs)
            {
                if (_stopping || _persistenceFailed) break;
                using var jobCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, _shutdown.Token);
                SetActiveJob(job, jobCancellation);

                try
                {
                    bool shouldProcess = false;
                    await RunOnDispatcherAsync(() =>
                    {
                        if (job.Status != "Queued")
                        {
                            return;
                        }

                        job.Status = "Scanning";
                        job.Progress = 5;
                        job.ErrorMessage = null;
                        job.OutputPath = null;
                        shouldProcess = true;
                    });
                    if (!shouldProcess)
                    {
                        continue;
                    }

                    job.JobOutputRoot ??= outputRoot;
                    await PersistJobAsync(job);
                    if (_queueStore is not null && await _fingerprints.GetFingerprintAsync(job.SourcePath, jobCancellation.Token) != job.SourceFingerprint)
                        throw new IOException("The source CBZ changed after it was queued. Add the changed file as a new job.");
                    string title = Path.GetFileNameWithoutExtension(
                        job.SourcePath);
                    ComicMetadata requestedMetadata =
                        job.ApprovedMetadata ??
                        job.RequestedMetadata ?? new ComicMetadata(title, job.Series);
                    var request = new ConversionRequest(
                        job.SourcePath,
                        job.JobOutputRoot,
                        requestedMetadata,
                        MetadataIsApproved: job.ApprovedMetadata is not null);
                    ConversionResult result = await Task.Run(
                        () => _conversionService.ConvertAsync(
                            request,
                            jobCancellation.Token,
                            _queueStore is null ? null : async (state, progress) =>
                            {
                                await RunOnDispatcherAsync(() => { job.Status = ConversionJobViewModel.DisplayState(state); job.Progress = progress; });
                                await PersistJobAsync(job);
                            },
                            _queueStore is null ? null : (path, hash) => PersistJobAsync(job, QueueJobState.Completed, path, hash)),
                        jobCancellation.Token);

                    await RunOnDispatcherAsync(() =>
                    {
                        job.OutputPath = result.OutputPath;
                        job.Progress = 100;
                        job.Status = "Completed";
                    });
                    await PersistJobAsync(job);
                }
                catch (MetadataReviewRequiredException exception)
                {
                    await RunOnDispatcherAsync(() =>
                    {
                        job.MetadataReview = new MetadataReviewViewModel(
                            exception.OfflineMetadata,
                            exception.Candidates);
                        job.Status = "Needs Review";
                        job.Progress = 0;
                        job.ErrorMessage = null;
                        job.OutputPath = null;
                        SelectedJob = job;
                    });
                    await PersistJobAsync(job);
                }
                catch (OperationCanceledException)
                {
                    await RunOnDispatcherAsync(() =>
                    {
                        job.Status = _stopping ? "Queued" : "Cancelled";
                        job.Progress = 0;
                        job.ErrorMessage = null;
                        job.OutputPath = null;
                    });
                    await PersistJobAsync(job);
                }
                catch (Exception exception)
                {
                    if (_persistenceFailed) throw;
                    await RunOnDispatcherAsync(() =>
                    {
                        job.ErrorStage = job.Status;
                        job.TechnicalError = SafeExceptionDetails.Format(exception);
                        job.Retryable = true;
                        job.Status = "Failed";
                        job.Progress = 0;
                        job.ErrorMessage = SafeExceptionDetails.Redact(exception.Message);
                        job.OutputPath = null;
                    });
                    await PersistJobAsync(job);
                }
                finally
                {
                    ClearActiveJob(job, jobCancellation);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _isProcessing, 0);
            await RunOnDispatcherAsync(RaiseQueueStateChanged);
        }
    }

    private static string[] DiscoverComicPaths(
        IEnumerable<string> requestedPaths)
    {
        var discovered = new List<string>();
        var uniquePaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };

        foreach (string requestedPath in requestedPaths)
        {
            string canonicalPath;
            try
            {
                canonicalPath = Path.GetFullPath(requestedPath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
            {
                continue;
            }

            if (File.Exists(canonicalPath))
            {
                AddComicPath(canonicalPath, discovered, uniquePaths);
                continue;
            }

            if (!Directory.Exists(canonicalPath))
            {
                continue;
            }

            try
            {
                foreach (string filePath in Directory
                    .EnumerateFiles(
                        canonicalPath,
                        "*",
                        enumerationOptions)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    AddComicPath(filePath, discovered, uniquePaths);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return [.. discovered];
    }

    private static void AddComicPath(
        string path,
        ICollection<string> discovered,
        ISet<string> uniquePaths)
    {
        if (!string.Equals(
                Path.GetExtension(path),
                ".cbz",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string canonicalPath = Path.GetFullPath(path);
        if (uniquePaths.Add(canonicalPath))
        {
            discovered.Add(canonicalPath);
        }
    }

    private async Task AddFilesFromPickerAsync()
    {
        IReadOnlyList<string> paths = _folderPicker.PickComicFiles();
        if (paths.Count > 0)
        {
            await AddPathsAsync(paths);
        }
    }

    private async Task AddFolderFromPickerAsync()
    {
        string? path = _folderPicker.PickFolder(
            "Add folder containing CBZ comics");
        if (path is not null)
        {
            await AddPathsAsync([path]);
        }
    }

    private Task BrowseOutputAsync()
    {
        string? selectedFolder = _folderPicker.PickFolder(
            "Choose EPUB output folder",
            OutputRoot);
        if (selectedFolder is not null)
        {
            OutputRoot = selectedFolder;
        }

        return Task.CompletedTask;
    }

    private async Task CancelSelectedAsync()
    {
        ConversionJobViewModel? selectedJob = SelectedJob;
        if (selectedJob is null)
        {
            return;
        }

        CancellationTokenSource? cancellation = null;
        lock (_activeJobLock)
        {
            if (ReferenceEquals(_activeJob, selectedJob))
            {
                cancellation = _activeJobCancellation;
            }
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
            return;
        }

        if (selectedJob.Status == "Queued")
        {
            selectedJob.Status = "Cancelled";
            selectedJob.Progress = 0;
            await PersistJobAsync(selectedJob);
        }

        return;
    }

    private Task SaveComicVineSettingsAsync() =>
        ComicVineSettings?.SaveAsync() ?? Task.CompletedTask;

    private Task DeleteComicVineSettingsAsync() =>
        ComicVineSettings?.DeleteAsync() ?? Task.CompletedTask;

    private async Task ApproveMetadataAsync()
    {
        var job = SelectedJob;
        if (job?.MetadataReview is null) return;
        ComicMetadata approved = job.MetadataReview.BuildMetadata();
        await ChangeJobAsync(job, () => { job.ApprovedMetadata = approved; ResetForRetry(job, incrementRetry: false); });
        RaiseQueueStateChanged();
    }

    private async Task RetrySelectedAsync()
    {
        if (SelectedJob is { } job)
        {
            await ChangeJobAsync(job, () => ResetForRetry(job));
        }
    }

    private async Task RetryAllFailedAsync()
    {
        foreach (var job in Jobs.Where(j => j.Status == "Failed").ToArray())
        {
            await ChangeJobAsync(job, () => ResetForRetry(job));
        }
    }

    private static void ResetForRetry(ConversionJobViewModel job, bool incrementRetry = true)
    {
        if (incrementRetry) job.RetryCount++;
        job.Progress = 0;
        job.ErrorMessage = null;
        job.OutputPath = null;
        job.MetadataReview = null;
        job.PersistedCandidates = null;
        job.ErrorStage = null;
        job.TechnicalError = null;
        job.Retryable = false;
        job.Status = "Queued";
    }

    private bool CanStart()
    {
        return !_stopping && !_persistenceFailed && _pendingMutations == 0 && _settingsWrite.IsCompletedSuccessfully && Volatile.Read(ref _isProcessing) == 0 &&
            !string.IsNullOrWhiteSpace(OutputRoot) &&
            Jobs.Any(job => job.Status == "Queued");
    }

    private bool CanCancelSelected()
    {
        return SelectedJob is { } selected && (selected.Status == "Queued" || ReferenceEquals(selected, _activeJob));
    }

    private bool CanRetrySelected()
    {
        return !_stopping && !_persistenceFailed && Volatile.Read(ref _isProcessing) == 0 && SelectedJob?.Status == "Failed";
    }

    private void SetActiveJob(
        ConversionJobViewModel job,
        CancellationTokenSource cancellation)
    {
        lock (_activeJobLock)
        {
            _activeJob = job;
            _activeJobCancellation = cancellation;
        }
    }

    private void ClearActiveJob(
        ConversionJobViewModel job,
        CancellationTokenSource cancellation)
    {
        lock (_activeJobLock)
        {
            if (ReferenceEquals(_activeJob, job) &&
                ReferenceEquals(_activeJobCancellation, cancellation))
            {
                _activeJob = null;
                _activeJobCancellation = null;
            }
        }
    }

    private void OnJobPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is
            nameof(ConversionJobViewModel.Status) or
            nameof(ConversionJobViewModel.Progress))
        {
            RaiseQueueStateChanged();
        }
    }

    private void RaiseQueueStateChanged()
    {
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(NeedsReviewCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(OverallProgress));
        _startCommand.RaiseCanExecuteChanged();
        _cancelSelectedCommand.RaiseCanExecuteChanged();
        _retrySelectedCommand.RaiseCanExecuteChanged();
        _retryAllFailedCommand.RaiseCanExecuteChanged();
        _approveMetadataCommand.RaiseCanExecuteChanged();
    }

    private Task RunOnDispatcherAsync(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private static void ReportCommandException(Exception exception)
    {
        System.Diagnostics.Trace.TraceError(
            "Unhandled command failure: {0}",
            SafeExceptionDetails.Format(exception));
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
