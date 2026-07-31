using ABC.Converter.App.ViewModels;
using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.ComicVine;
using Xunit;

namespace ABC.Converter.App.Tests.ViewModels;

public sealed class ComicVineSettingsViewModelTests
{
    [Fact]
    public async Task SavesAndDeletesApiKeyWithoutPersistingItInTheViewModel()
    {
        var store = new RecordingKeyStore();
        var viewModel = new ComicVineSettingsViewModel(store)
        {
            ApiKey = "  secret-key  ",
        };

        await viewModel.SaveAsync();

        Assert.Equal("secret-key", store.SavedValue);
        Assert.True(viewModel.IsConfigured);
        Assert.Equal(string.Empty, viewModel.ApiKey);

        await viewModel.DeleteAsync();

        Assert.True(store.DeleteCalled);
        Assert.False(viewModel.IsConfigured);
    }

    private sealed class RecordingKeyStore : IComicVineApiKeyStore
    {
        public string? SavedValue { get; private set; }

        public bool DeleteCalled { get; private set; }

        public void Save(string apiKey) => SavedValue = apiKey;

        public string? Load() => SavedValue;

        public void Delete()
        {
            DeleteCalled = true;
            SavedValue = null;
        }
    }
}

public sealed class MetadataReviewViewModelTests
{
    [Fact]
    public void CandidateCanPopulateEditableMetadataAndBuildAnApprovedResult()
    {
        var offline = new ComicMetadata("Batman 1", "Batman", "1", publicationYear: 2016);
        var candidate = new ComicVineIssue(
            101,
            "I Am Gotham, Part One",
            "1",
            "Batman",
            2016);
        var viewModel = new MetadataReviewViewModel(offline, [candidate]);

        viewModel.ApplyCandidate(candidate);
        viewModel.Title = "I Am Gotham";

        ComicMetadata approved = viewModel.BuildMetadata();

        Assert.Equal("I Am Gotham", approved.Title);
        Assert.Equal("Batman", approved.Series);
        Assert.Equal("1", approved.IssueNumber);
        Assert.Equal(2016, approved.PublicationYear);
    }

    [Fact]
    public void RejectsBlankRequiredFields()
    {
        var viewModel = new MetadataReviewViewModel(
            new ComicMetadata("Batman 1", "Batman"),
            []);

        viewModel.Series = " ";

        Assert.Throws<InvalidOperationException>(() => viewModel.BuildMetadata());
    }
}
