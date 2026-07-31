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

    public MainWindowViewModel(
        ComicConversionService conversionService,
        FolderPicker? folderPicker = null,
        Dispatcher? dispatcher = null,
        Action<Exception>? commandExceptionHandler = null,
        ComicVineSettingsViewModel? comicVineSettings = null)
    {
        _conversionService = conversionService;
        _folderPicker = folderPicker ?? new FolderPicker();
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher;
        ComicVineSettings = comicVineSettings;
        Action<Exception> onCommandException =
            commandExceptionHandler ?? ReportCommandException;
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
            () => Jobs.Any(job => job.Status == "Failed"),
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
            () => SelectedJob?.MetadataReview is not null,
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
            if (SetField(ref _outputRoot, value))
            {
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

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        string[] requestedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        string[] discoveredPaths = await Task.Run(
            () => DiscoverComicPaths(requestedPaths));

        await RunOnDispatcherAsync(() =>
        {
            foreach (string path in discoveredPaths)
            {
                if (!_knownSourcePaths.Add(path))
                {
                    continue;
                }

                string title = Path.GetFileNameWithoutExtension(path);
                var job = new ConversionJobViewModel
                {
                    SourcePath = path,
                    Series = title,
                };
                job.PropertyChanged += OnJobPropertyChanged;
                Jobs.Add(job);
            }

            RaiseQueueStateChanged();
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken)
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
                using var jobCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
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

                    string title = Path.GetFileNameWithoutExtension(
                        job.SourcePath);
                    ComicMetadata requestedMetadata =
                        job.ApprovedMetadata ??
                        new ComicMetadata(title, job.Series);
                    var request = new ConversionRequest(
                        job.SourcePath,
                        outputRoot,
                        requestedMetadata,
                        MetadataIsApproved: job.ApprovedMetadata is not null);
                    ConversionResult result = await Task.Run(
                        () => _conversionService.ConvertAsync(
                            request,
                            jobCancellation.Token),
                        jobCancellation.Token);

                    await RunOnDispatcherAsync(() =>
                    {
                        job.OutputPath = result.OutputPath;
                        job.Progress = 100;
                        job.Status = "Completed";
                    });
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
                }
                catch (OperationCanceledException)
                {
                    await RunOnDispatcherAsync(() =>
                    {
                        job.Status = "Cancelled";
                        job.Progress = 0;
                        job.ErrorMessage = null;
                        job.OutputPath = null;
                    });
                }
                catch (Exception exception)
                {
                    await RunOnDispatcherAsync(() =>
                    {
                        job.Status = "Failed";
                        job.Progress = 0;
                        job.ErrorMessage = exception.Message;
                        job.OutputPath = null;
                    });
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

    private Task CancelSelectedAsync()
    {
        ConversionJobViewModel? selectedJob = SelectedJob;
        if (selectedJob is null)
        {
            return Task.CompletedTask;
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
            return Task.CompletedTask;
        }

        if (selectedJob.Status == "Queued")
        {
            selectedJob.Status = "Cancelled";
            selectedJob.Progress = 0;
        }

        return Task.CompletedTask;
    }

    private Task SaveComicVineSettingsAsync() =>
        ComicVineSettings?.SaveAsync() ?? Task.CompletedTask;

    private Task DeleteComicVineSettingsAsync() =>
        ComicVineSettings?.DeleteAsync() ?? Task.CompletedTask;

    private Task ApproveMetadataAsync()
    {
        ConversionJobViewModel? job = SelectedJob;
        if (job?.MetadataReview is null)
        {
            return Task.CompletedTask;
        }

        job.ApprovedMetadata = job.MetadataReview.BuildMetadata();
        job.MetadataReview = null;
        job.Status = "Queued";
        job.Progress = 0;
        job.ErrorMessage = null;
        _approveMetadataCommand.RaiseCanExecuteChanged();
        RaiseQueueStateChanged();
        return Task.CompletedTask;
    }

    private Task RetrySelectedAsync()
    {
        if (SelectedJob is not null)
        {
            ResetForRetry(SelectedJob);
        }

        return Task.CompletedTask;
    }

    private Task RetryAllFailedAsync()
    {
        foreach (ConversionJobViewModel job in Jobs.Where(
                     job => job.Status == "Failed"))
        {
            ResetForRetry(job);
        }

        return Task.CompletedTask;
    }

    private static void ResetForRetry(ConversionJobViewModel job)
    {
        job.Progress = 0;
        job.ErrorMessage = null;
        job.OutputPath = null;
        job.MetadataReview = null;
        job.ApprovedMetadata = null;
        job.Status = "Queued";
    }

    private bool CanStart()
    {
        return Volatile.Read(ref _isProcessing) == 0 &&
            !string.IsNullOrWhiteSpace(OutputRoot) &&
            Jobs.Any(job => job.Status == "Queued");
    }

    private bool CanCancelSelected()
    {
        return SelectedJob?.Status is "Queued" or "Scanning";
    }

    private bool CanRetrySelected()
    {
        return SelectedJob?.Status == "Failed";
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
            exception);
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
