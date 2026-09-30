using Backup.Application.Providers;
using Backup.Providers.Files.Local;

namespace Backup.Tests;

public sealed class LocalFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("local-").FullName;
    private readonly ProviderSettings _settings = new(new Dictionary<string, string?>());

    [Fact]
    public async Task CreateFolder_CreatesInsideParent_AndRejectsDuplicates()
    {
        IFolderCreator destination = new LocalFolderDestination();

        var created = await destination.CreateFolderAsync(_settings, _root, " Respaldos 2026 ", CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "Respaldos 2026"), created);
        Assert.True(Directory.Exists(created));
        await Assert.ThrowsAsync<IOException>(() => destination.CreateFolderAsync(_settings, _root, "Respaldos 2026", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public async Task CreateFolder_RejectsNamesThatAreNotASingleFolder(string name)
    {
        IFolderCreator source = new LocalFolderSource();

        await Assert.ThrowsAsync<ArgumentException>(() => source.CreateFolderAsync(_settings, _root, name, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
