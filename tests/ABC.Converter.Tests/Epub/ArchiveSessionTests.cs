using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Models;
using ABC.Converter.Epub;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Tests.Fixtures;
using Xunit;

namespace ABC.Converter.Tests.Epub;

public sealed class ArchiveSessionTests
{
    [Fact]
    public async Task BuildOpensOneSessionForAllPagesAndDisposesIt()
    {
        string cbz = FixtureArchive.CreateCbz(
            "page1.png",
            "page2.png",
            "page3.png");
        string epub = Path.Combine(
            Path.GetTempPath(),
            $"ABC.Converter.Tests-{Guid.NewGuid():N}.epub");

        try
        {
            var inner = new CbzArchiveReader();
            ComicSource source = await inner.ReadAsync(
                cbz,
                CancellationToken.None);
            var reader = new TrackingArchiveReader(inner);
            var builder = new FixedLayoutEpubBuilder(reader);

            await builder.BuildAsync(
                source,
                new ComicMetadata("Issue 1", "Example Series"),
                ReadingDirection.LeftToRight,
                epub,
                CancellationToken.None);

            Assert.Equal(1, reader.OpenSessionCount);
            Assert.NotNull(reader.Session);
            Assert.Equal(source.Pages.Count, reader.Session.ExtractionCount);
            Assert.True(reader.Session.IsDisposed);
        }
        finally
        {
            File.Delete(epub);
            File.Delete(cbz);
        }
    }

    private sealed class TrackingArchiveReader(
        IComicArchiveReader inner) : IComicArchiveReader
    {
        public int OpenSessionCount { get; private set; }

        public TrackingSession? Session { get; private set; }

        public Task<ComicSource> ReadAsync(
            string sourcePath,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(sourcePath, cancellationToken);

        public Task ExtractPageAsync(
            string sourcePath,
            ComicPage page,
            Stream destination,
            CancellationToken cancellationToken) =>
            inner.ExtractPageAsync(
                sourcePath,
                page,
                destination,
                cancellationToken);

        public async Task<IComicArchiveSession> OpenSessionAsync(
            string sourcePath,
            CancellationToken cancellationToken)
        {
            OpenSessionCount++;
            IComicArchiveSession session = await inner.OpenSessionAsync(
                sourcePath,
                cancellationToken);
            Session = new TrackingSession(session);
            return Session;
        }
    }

    private sealed class TrackingSession(
        IComicArchiveSession inner) : IComicArchiveSession
    {
        public int ExtractionCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public async Task ExtractPageAsync(
            ComicPage page,
            Stream destination,
            CancellationToken cancellationToken)
        {
            ExtractionCount++;
            await inner.ExtractPageAsync(
                page,
                destination,
                cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            IsDisposed = true;
        }
    }
}
