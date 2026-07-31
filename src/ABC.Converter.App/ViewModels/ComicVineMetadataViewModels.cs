using System.ComponentModel;
using System.Runtime.CompilerServices;
using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.ComicVine;

namespace ABC.Converter.App.ViewModels;

public sealed class ComicVineSettingsViewModel : INotifyPropertyChanged
{
    private readonly IComicVineApiKeyStore store;
    private string apiKey = string.Empty;
    private bool isConfigured;

    public ComicVineSettingsViewModel(IComicVineApiKeyStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        isConfigured = !string.IsNullOrWhiteSpace(store.Load());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ApiKey
    {
        get => apiKey;
        set => SetField(ref apiKey, value);
    }

    public bool IsConfigured
    {
        get => isConfigured;
        private set => SetField(ref isConfigured, value);
    }

    public Task SaveAsync()
    {
        string value = ApiKey.Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException("Enter a ComicVine API key before saving.");
        }

        store.Save(value);
        ApiKey = string.Empty;
        IsConfigured = true;
        return Task.CompletedTask;
    }

    public Task DeleteAsync()
    {
        store.Delete();
        ApiKey = string.Empty;
        IsConfigured = false;
        return Task.CompletedTask;
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class MetadataReviewViewModel : INotifyPropertyChanged
{
    private string title;
    private string series;
    private string? issueNumber;
    private string? volume;
    private int? publicationYear;
    private string language;

    public MetadataReviewViewModel(
        ComicMetadata offlineMetadata,
        IReadOnlyList<ComicVineIssue> candidates)
    {
        ArgumentNullException.ThrowIfNull(offlineMetadata);
        Candidates = candidates ??
            throw new ArgumentNullException(nameof(candidates));
        title = offlineMetadata.Title;
        series = offlineMetadata.Series;
        issueNumber = offlineMetadata.IssueNumber;
        volume = offlineMetadata.Volume;
        publicationYear = offlineMetadata.PublicationYear;
        language = offlineMetadata.Language;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ComicVineIssue> Candidates { get; }

    public string Title
    {
        get => title;
        set => SetField(ref title, value);
    }

    public string Series
    {
        get => series;
        set => SetField(ref series, value);
    }

    public string? IssueNumber
    {
        get => issueNumber;
        set => SetField(ref issueNumber, value);
    }

    public string? Volume
    {
        get => volume;
        set => SetField(ref volume, value);
    }

    public int? PublicationYear
    {
        get => publicationYear;
        set => SetField(ref publicationYear, value);
    }

    public string Language
    {
        get => language;
        set => SetField(ref language, value);
    }

    public void ApplyCandidate(ComicVineIssue candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Title = string.IsNullOrWhiteSpace(candidate.Name) ? Title : candidate.Name;
        Series = string.IsNullOrWhiteSpace(candidate.Series) ? Series : candidate.Series;
        IssueNumber = candidate.IssueNumber ?? IssueNumber;
        PublicationYear = candidate.StartYear ?? PublicationYear;
    }

    public ComicMetadata BuildMetadata()
    {
        if (string.IsNullOrWhiteSpace(Title) ||
            string.IsNullOrWhiteSpace(Series))
        {
            throw new InvalidOperationException("Title and series are required.");
        }

        return new ComicMetadata(
            Title.Trim(),
            Series.Trim(),
            NullIfWhiteSpace(IssueNumber),
            NullIfWhiteSpace(Volume),
            PublicationYear,
            string.IsNullOrWhiteSpace(Language) ? "en" : Language.Trim());
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
