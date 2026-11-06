namespace Hodozip.Core.Tests;

/// <summary>MSI (Windows Installer) パッケージの解凍テスト。</summary>
public sealed class MsiExtractionTests : IDisposable
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "formats");

    private readonly string _root;

    public MsiExtractionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HodozipMsiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // テスト後の後片付け失敗は無視する。
        }
    }

    [Fact]
    public async Task Msi_ExtractsExpectedContent()
    {
        var destination = Path.Combine(_root, "msi");

        var result = await ExtractAsync(Fixture("sample.msi"), destination);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(4, result.ExtractedFileCount);

        // ProgramFiles64Folder は「Program Files」、INSTALLDIR は「Sample」に解決される。
        var installRoot = Path.Combine(destination, "Program Files", "Sample");
        Assert.Equal("hello world", await File.ReadAllTextAsync(Path.Combine(installRoot, "hello.txt")));
        Assert.Equal("A content", await File.ReadAllTextAsync(Path.Combine(installRoot, "Top", "a.txt")));
        Assert.Equal("B content", await File.ReadAllTextAsync(Path.Combine(installRoot, "Top", "sub", "b.txt")));
        Assert.Equal("こんにちは", await File.ReadAllTextAsync(Path.Combine(installRoot, "日本語テスト.txt")));
    }

    [Fact]
    public async Task Msi_UsesFriendlyStandardFolderNames()
    {
        var destination = Path.Combine(_root, "friendly");

        var result = await ExtractAsync(Fixture("sample.msi"), destination);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(Path.Combine(destination, "Program Files"), result.DestinationPath);
        Assert.False(Directory.Exists(Path.Combine(destination, "PFiles64")));
        Assert.False(Directory.Exists(Path.Combine(destination, "ProgramFiles64Folder")));
    }

    [Fact]
    public async Task Msi_ExternalCabinet_ExtractsExpectedContent()
    {
        // 外部キャビネットは MSI と同じディレクトリに置かれている必要がある。
        var archiveDirectory = Path.Combine(_root, "external");
        Directory.CreateDirectory(archiveDirectory);
        var archivePath = Path.Combine(archiveDirectory, "sample-external.msi");
        File.Copy(Fixture(Path.Combine("msi-external", "sample-external.msi")), archivePath);
        File.Copy(
            Fixture(Path.Combine("msi-external", "cab1.cab")),
            Path.Combine(archiveDirectory, "cab1.cab"));

        var destination = Path.Combine(_root, "external-out");

        var result = await ExtractAsync(archivePath, destination);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(4, result.ExtractedFileCount);

        var installRoot = Path.Combine(destination, "Program Files", "SampleExternal");
        Assert.Equal("hello world", await File.ReadAllTextAsync(Path.Combine(installRoot, "hello.txt")));
        Assert.Equal("A content", await File.ReadAllTextAsync(Path.Combine(installRoot, "Top", "a.txt")));
        Assert.Equal("B content", await File.ReadAllTextAsync(Path.Combine(installRoot, "Top", "sub", "b.txt")));
        Assert.Equal("こんにちは", await File.ReadAllTextAsync(Path.Combine(installRoot, "日本語テスト.txt")));
    }

    [Fact]
    public async Task Msi_CorruptFile_ReturnsArchiveError()
    {
        var archive = Path.Combine(_root, "broken.msi");
        await File.WriteAllTextAsync(archive, "this is not an MSI package");
        var destination = Path.Combine(_root, "broken");

        var result = await ExtractAsync(archive, destination);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, x =>
            x.Severity == ExtractionIssueSeverity.Error
            && x.Kind == ExtractionIssueKind.ArchiveError);
    }

    private static string Fixture(string name) => Path.Combine(FixtureDirectory, name);

    private static async Task<ExtractionResult> ExtractAsync(string archivePath, string destination)
    {
        return await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(archivePath, destination, new ExtractionOptions
            {
                OverwritePolicy = OverwritePolicy.Overwrite,
                FolderCollisionPolicy = FolderCollisionPolicy.Merge,
            }));
    }

    private static string Describe(ExtractionResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Issues.Select(x => $"{x.Severity} {x.Kind} {x.EntryPath}: {x.Message}"));
    }
}
