using System.ComponentModel;
using System.Runtime.CompilerServices;
using ABC.Converter.Core.Models;
using ABC.Converter.Core.Queue;
using ABC.Converter.Infrastructure.ComicVine;
using System.IO;

namespace ABC.Converter.App.ViewModels;

public sealed class ConversionJobViewModel : INotifyPropertyChanged
{
    private string _sourcePath = string.Empty;
    private string _series = string.Empty;
    private string _status = "Queued";
    private int _progress;
    private string? _errorMessage;
    private string? _outputPath;
    private ComicMetadata? _approvedMetadata;
    private MetadataReviewViewModel? _metadataReview;

    public Guid Id { get; init; } = Guid.NewGuid();
    public string SourceFingerprint { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; internal set; } = DateTimeOffset.UtcNow;
    public int RetryCount { get; internal set; }
    public string? ErrorStage { get; internal set; }
    public string? TechnicalError { get; internal set; }
    public bool Retryable { get; internal set; }
    internal ComicMetadata? RequestedMetadata { get; set; }
    internal string? JobOutputRoot { get; set; }

    internal static QueueJobState ParseState(string status) => status switch
    {
        "Needs Review" => QueueJobState.NeedsReview,
        _ when Enum.TryParse<QueueJobState>(status, out var state) && Enum.IsDefined(state) && state.ToString() == status => state,
        _ => throw new InvalidOperationException($"Unknown queue status: {status}")
    };

    internal static string DisplayState(QueueJobState state) => state == QueueJobState.NeedsReview ? "Needs Review" :
        Enum.IsDefined(state) ? state.ToString() : throw new InvalidOperationException("Unknown queue state.");

    internal QueueJobRecord ToRecord(string outputRoot, DateTimeOffset utcNow)
    {
        UpdatedUtc = utcNow;
        QueueMetadataReview? review = MetadataReview is null ? null : new(
            MetadataReview.BuildMetadata(), MetadataReview.Candidates.Select(c => new QueueMetadataCandidate(
                "ComicVine", c.Id, c.Name, c.IssueNumber, c.Series, c.StartYear)).ToArray());
        return new(Id, SourcePath, SourceFingerprint, JobOutputRoot ?? outputRoot,
            RequestedMetadata ?? new ComicMetadata(Path.GetFileNameWithoutExtension(SourcePath), Series),
            ParseState(Status), Progress, CreatedUtc, UpdatedUtc, ApprovedMetadata, review,
            OutputPath, ErrorStage, ErrorMessage, TechnicalError, Retryable, RetryCount);
    }

    internal static ConversionJobViewModel FromRecord(QueueJobRecord record) => new()
    {
        Id = record.Id, SourcePath = record.SourcePath, SourceFingerprint = record.SourceFingerprint,
        Series = record.RequestedMetadata.Series, RequestedMetadata = record.RequestedMetadata,
        JobOutputRoot = record.OutputRoot, Status = DisplayState(record.State), Progress = record.Progress,
        CreatedUtc = record.CreatedUtc, UpdatedUtc = record.UpdatedUtc, ApprovedMetadata = record.ApprovedMetadata,
        MetadataReview = record.MetadataReview is null ? null : new MetadataReviewViewModel(
            record.MetadataReview.OfflineMetadata, record.MetadataReview.Candidates.Select(c => new ComicVineIssue(
                c.ExternalId, c.Name, c.IssueNumber, c.Series, c.StartYear)).ToArray()),
        OutputPath = record.OutputPath, ErrorMessage = record.UserError, ErrorStage = record.ErrorStage,
        TechnicalError = record.TechnicalError, RetryCount = record.RetryCount, Retryable = record.Retryable
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourcePath
    {
        get => _sourcePath;
        set => SetField(ref _sourcePath, value);
    }

    public string Series
    {
        get => _series;
        set => SetField(ref _series, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public int Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public string? OutputPath
    {
        get => _outputPath;
        set => SetField(ref _outputPath, value);
    }

    public ComicMetadata? ApprovedMetadata
    {
        get => _approvedMetadata;
        set => SetField(ref _approvedMetadata, value);
    }

    public MetadataReviewViewModel? MetadataReview
    {
        get => _metadataReview;
        set => SetField(ref _metadataReview, value);
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
