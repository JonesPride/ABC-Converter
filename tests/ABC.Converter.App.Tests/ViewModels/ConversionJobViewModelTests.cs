using System.ComponentModel;
using ABC.Converter.App.ViewModels;
using Xunit;

namespace ABC.Converter.App.Tests.ViewModels;

public sealed class ConversionJobViewModelTests
{
    [Fact]
    public void EveryJobPropertyRaisesItsOwnPropertyChangedNotification()
    {
        var job = new ConversionJobViewModel();
        var notifications = new List<string?>();
        job.PropertyChanged += (_, eventArgs) => notifications.Add(eventArgs.PropertyName);

        job.SourcePath = "comic.cbz";
        job.Series = "Example Series";
        job.Status = "Scanning";
        job.Progress = 42;
        job.ErrorMessage = "Example failure";
        job.OutputPath = "comic.epub";

        Assert.Equal(
            [
                nameof(job.SourcePath),
                nameof(job.Series),
                nameof(job.Status),
                nameof(job.Progress),
                nameof(job.ErrorMessage),
                nameof(job.OutputPath),
            ],
            notifications);
        Assert.IsAssignableFrom<INotifyPropertyChanged>(job);
    }
}
