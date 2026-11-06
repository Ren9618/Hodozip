using System.IO.Compression;
using System.Text;

namespace Hodozip.Core.Tests;

public sealed class ExtractionTests : IDisposable
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly string _root;

    static ExtractionTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ExtractionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HodozipTests", Guid.NewGuid().ToString("N"));
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
    public async Task SingleFileZip_ExtractsDirectlyToDestination()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = ZipFactory.Create(archiveDirectory, "single.zip", ("hello.txt", "hello"));

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.Equal(1, result.ExtractedFileCount);
        Assert.Equal(Path.GetFullPath(destination), Path.GetFullPath(result.DestinationPath));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(destination, "hello.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "single")));
    }

    [Fact]
    public async Task SingleFolderZip_ExtractsWithoutDoubleFolder()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = ZipFactory.Create(
            archiveDirectory,
            "folder.zip",
            ("Top/a.txt", "A"),
            ("Top/sub/b.txt", "B"),
            ("Top/empty/", null));

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(destination, "Top"), result.DestinationPath);
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(destination, "Top", "a.txt")));
        Assert.Equal("B", await File.ReadAllTextAsync(Path.Combine(destination, "Top", "sub", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(destination, "Top", "empty")));
        Assert.False(Directory.Exists(Path.Combine(destination, "folder", "Top")));
        Assert.False(Directory.Exists(Path.Combine(destination, "folder")));
    }

    [Fact]
    public async Task ScatteredZip_CreatesArchiveNamedFolder()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = ZipFactory.Create(
            archiveDirectory,
            "Scattered.zip",
            ("a.txt", "A"),
            ("b.txt", "B"));

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(destination, "Scattered"), result.DestinationPath);
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(destination, "Scattered", "a.txt")));
        Assert.Equal("B", await File.ReadAllTextAsync(Path.Combine(destination, "Scattered", "b.txt")));
    }

    [Fact]
    public async Task ForceArchiveFolder_SingleFileZip_CreatesArchiveFolder()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = ZipFactory.Create(archiveDirectory, "single.zip", ("hello.txt", "hello"));

        var result = await ExtractAsync(
            archive,
            destination,
            new ExtractionOptions(),
            ExtractionDestinationMode.ForceArchiveFolder);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(destination, "single"), result.DestinationPath);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(destination, "single", "hello.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "hello.txt")));
    }

    [Fact]
    public async Task ForceArchiveFolder_SingleTopFolderZip_KeepsArchiveFolder()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = ZipFactory.Create(
            archiveDirectory,
            "folder.zip",
            ("Top/a.txt", "A"),
            ("Top/sub/b.txt", "B"));

        var result = await ExtractAsync(
            archive,
            destination,
            new ExtractionOptions(),
            ExtractionDestinationMode.ForceArchiveFolder);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(destination, "folder"), result.DestinationPath);
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(destination, "folder", "Top", "a.txt")));
        Assert.Equal("B", await File.ReadAllTextAsync(Path.Combine(destination, "folder", "Top", "sub", "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "Top")));
        Assert.False(Directory.Exists(Path.Combine(destination, "folder", "folder")));
    }

    [Fact]
    public async Task ForceArchiveFolder_ExistingArchiveFolder_GetsNumberedSuffix()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.Combine(destination, "folder"));

        var archive = ZipFactory.Create(archiveDirectory, "folder.zip", ("Top/a.txt", "A"));

        var result = await ExtractAsync(
            archive,
            destination,
            new ExtractionOptions(),
            ExtractionDestinationMode.ForceArchiveFolder);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(destination, "folder (2)"), result.DestinationPath);
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(destination, "folder (2)", "Top", "a.txt")));
    }

    [Fact]
    public async Task MultipleTopFolders_AreTreatedAsScattered()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = ZipFactory.Create(
            archiveDirectory,
            "multi.zip",
            ("A/a.txt", "A"),
            ("B/b.txt", "B"));

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(destination, "multi"), result.DestinationPath);
        Assert.True(File.Exists(Path.Combine(destination, "multi", "A", "a.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "multi", "B", "b.txt")));
    }

    [Fact]
    public async Task ExistingSingleFolder_GetsNumberedSuffix()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.Combine(destination, "Top"));
        await File.WriteAllTextAsync(Path.Combine(destination, "Top", "existing.txt"), "existing");

        var archive = ZipFactory.Create(archiveDirectory, "folder.zip", ("Top/a.txt", "A"));

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(destination, "Top (2)"), result.DestinationPath);
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(destination, "Top (2)", "a.txt")));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "Top", "existing.txt")));
    }

    [Fact]
    public async Task ExistingScatteredFolder_GetsNumberedSuffix()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(Path.Combine(destination, "Scattered"));

        var archive = ZipFactory.Create(
            archiveDirectory,
            "Scattered.zip",
            ("a.txt", "A"),
            ("b.txt", "B"));

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.Equal(Path.Combine(destination, "Scattered (2)"), result.DestinationPath);
        Assert.True(File.Exists(Path.Combine(destination, "Scattered (2)", "a.txt")));
    }

    [Fact]
    public async Task ShiftJisFileName_DecodesWithoutMojibake()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = Path.Combine(archiveDirectory, "sjis.zip");
        Directory.CreateDirectory(archiveDirectory);

        var zip = new TestZip();
        zip.AddStored("日本語テスト.txt", "こんにちは", Encoding.GetEncoding(932), utf8Flag: false);
        await File.WriteAllBytesAsync(archive, zip.Build());

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        var expected = Path.Combine(destination, "日本語テスト.txt");
        Assert.True(File.Exists(expected));
        Assert.Equal("こんにちは", await File.ReadAllTextAsync(expected));
    }

    [Fact]
    public async Task Utf8FileName_DecodesCorrectly()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = ZipFactory.Create(archiveDirectory, "utf8.zip", ("日本語UTF8.txt", "hello"));

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        var expected = Path.Combine(destination, "日本語UTF8.txt");
        Assert.True(File.Exists(expected));
        Assert.Equal("hello", await File.ReadAllTextAsync(expected));
    }

    [Fact]
    public async Task ZipSlipEntry_IsRejected()
    {
        var parent = Path.Combine(_root, "slip");
        var destination = Path.Combine(parent, "dest");
        var archive = Path.Combine(parent, "slip.zip");
        Directory.CreateDirectory(parent);

        var zip = new TestZip();
        zip.AddStored("../evil.txt", "evil");
        await File.WriteAllBytesAsync(archive, zip.Build());

        var result = await ExtractAsync(archive, destination);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, x => x.Kind == ExtractionIssueKind.PathTraversal);
        Assert.False(File.Exists(Path.Combine(parent, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "..", "evil.txt")));
    }

    [Theory]
    [InlineData("password-zipcrypto.zip")]
    [InlineData("password-aes.zip")]
    public async Task PasswordZip_ExtractsWithCorrectPassword(string fixtureName)
    {
        var destination = Path.Combine(_root, "dest");
        var options = new ExtractionOptions
        {
            PasswordProvider = (_, _) => Task.FromResult<string?>("secret"),
        };

        var result = await ExtractAsync(Path.Combine(FixtureDirectory, fixtureName), destination, options);

        Assert.True(result.Success);
        Assert.Equal("Hodozip password test", await File.ReadAllTextAsync(Path.Combine(destination, "secret.txt")));
    }

    [Theory]
    [InlineData("password-zipcrypto.zip")]
    [InlineData("password-aes.zip")]
    public async Task WrongPassword_CanRetryWithAnotherPassword(string fixtureName)
    {
        var destination = Path.Combine(_root, "dest");
        var attempts = 0;
        var options = new ExtractionOptions
        {
            PasswordProvider = (_, _) =>
            {
                attempts++;
                return Task.FromResult<string?>(attempts == 1 ? "wrong" : "secret");
            },
        };

        var result = await ExtractAsync(Path.Combine(FixtureDirectory, fixtureName), destination, options);

        Assert.True(result.Success);
        Assert.Equal(2, attempts);
        Assert.Equal("Hodozip password test", await File.ReadAllTextAsync(Path.Combine(destination, "secret.txt")));
    }

    [Fact]
    public async Task PasswordCancelled_ReturnsCancelledResult()
    {
        var destination = Path.Combine(_root, "dest");
        var options = new ExtractionOptions
        {
            PasswordProvider = (_, _) => Task.FromResult<string?>(null),
        };

        var result = await ExtractAsync(Path.Combine(FixtureDirectory, "password-zipcrypto.zip"), destination, options);

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(destination, "secret.txt")));
        Assert.Contains(result.Issues, x => x.Kind == ExtractionIssueKind.Cancelled);
    }

    [Fact]
    public async Task OverwritePolicy_Overwrite_ReplacesExistingFile()
    {
        var destination = await PrepareExistingFile();
        var archive = CreateHelloZip();

        var result = await ExtractAsync(archive, destination, new ExtractionOptions
        {
            OverwritePolicy = OverwritePolicy.Overwrite,
        });

        Assert.True(result.Success);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination, "hello.txt")));
    }

    [Fact]
    public async Task OverwritePolicy_Skip_KeepsExistingFile()
    {
        var destination = await PrepareExistingFile();
        var archive = CreateHelloZip();

        var result = await ExtractAsync(archive, destination, new ExtractionOptions
        {
            OverwritePolicy = OverwritePolicy.Skip,
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(destination, "hello.txt")));
    }

    [Fact]
    public async Task OverwritePolicy_AutoRename_CreatesNumberedFile()
    {
        var destination = await PrepareExistingFile();
        var archive = CreateHelloZip();

        var result = await ExtractAsync(archive, destination, new ExtractionOptions
        {
            OverwritePolicy = OverwritePolicy.AutoRename,
        });

        Assert.True(result.Success);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(destination, "hello.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination, "hello (2).txt")));
    }

    [Fact]
    public async Task OverwritePolicy_Ask_ProvidesComparisonAndHonorsDecision()
    {
        var destination = await PrepareExistingFile();
        var archive = CreateHelloZip();
        OverwriteConflict? conflict = null;

        var result = await ExtractAsync(archive, destination, new ExtractionOptions
        {
            OverwritePolicy = OverwritePolicy.Ask,
            OverwriteResolver = (value, _) =>
            {
                conflict = value;
                return Task.FromResult(OverwriteDecision.AutoRename);
            },
        });

        Assert.True(result.Success);
        Assert.NotNull(conflict);
        Assert.Equal("hello.txt", conflict!.EntryPath);
        Assert.Equal(3, conflict.ExistingSize);
        Assert.Equal(3, conflict.NewSize);
        Assert.NotNull(conflict.ExistingLastWriteTime);
        Assert.NotNull(conflict.NewLastWriteTime);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destination, "hello (2).txt")));
    }

    [Fact]
    public async Task Cancellation_RemovesPartialFile()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var data = new byte[16 * 1024 * 1024];
        Random.Shared.NextBytes(data);
        var archive = ZipFactory.CreateWithBytes(archiveDirectory, "big.zip", "big.bin", data);

        using var cancellation = new CancellationTokenSource();
        var options = new ExtractionOptions
        {
            OverwritePolicy = OverwritePolicy.Overwrite,
            Progress = new InlineProgress<ExtractionProgress>(_ => cancellation.Cancel()),
        };

        var result = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(archive, destination, options),
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.False(File.Exists(Path.Combine(destination, "big.bin")));
        Assert.Empty(Directory.Exists(destination)
            ? Directory.GetFiles(destination, "~hodozip-*.tmp", SearchOption.AllDirectories)
            : Array.Empty<string>());
    }

    [Fact]
    public async Task ReservedDeviceName_IsSanitized()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = Path.Combine(archiveDirectory, "reserved.zip");
        Directory.CreateDirectory(archiveDirectory);

        var zip = new TestZip();
        zip.AddStored("CON.txt", "reserved");
        await File.WriteAllBytesAsync(archive, zip.Build());

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.Contains(result.Issues, x => x.Kind == ExtractionIssueKind.UnsafeName);
        Assert.True(File.Exists(Path.Combine(destination, "CON_.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "CON.txt")));
    }

    [Fact]
    public async Task SymbolicLinkEntry_IsSkipped()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = Path.Combine(archiveDirectory, "symlink.zip");
        Directory.CreateDirectory(archiveDirectory);

        var zip = new TestZip();
        zip.AddStored("link.txt", "target.txt", externalAttributes: unchecked((int)0xA1FF0000));
        await File.WriteAllBytesAsync(archive, zip.Build());

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.Contains(result.Issues, x => x.Kind == ExtractionIssueKind.SymbolicLink);
        Assert.False(File.Exists(Path.Combine(destination, "link.txt")));
    }

    [Fact]
    public async Task SuspiciousExpansion_ReturnsWarning()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        var archive = ZipFactory.CreateWithBytes(
            archiveDirectory,
            "bomb.zip",
            "big.bin",
            new byte[4 * 1024 * 1024]);

        var result = await ExtractAsync(archive, destination, new ExtractionOptions
        {
            ExpansionWarningMinBytes = 1024,
            ExpansionWarningRatio = 10,
        });

        Assert.True(result.Success);
        Assert.Contains(result.Issues, x => x.Kind == ExtractionIssueKind.SuspiciousExpansion);
        Assert.True(File.Exists(Path.Combine(destination, "big.bin")));
    }

    [Fact]
    public async Task LongPath_ExtractsSuccessfully()
    {
        var archiveDirectory = Path.Combine(_root, "long", "archives");
        var destination = Path.Combine(_root, "long", "dest");
        var archive = Path.Combine(archiveDirectory, "long.zip");
        Directory.CreateDirectory(archiveDirectory);

        var relativePath = string.Join('\\', Enumerable.Range(0, 35).Select(i => $"folder{i:D2}")) + "\\long.txt";
        Assert.True(relativePath.Length > 260);

        var zip = new TestZip();
        zip.AddStored(relativePath.Replace('\\', '/'), "long path");
        await File.WriteAllBytesAsync(archive, zip.Build());

        var result = await ExtractAsync(archive, destination);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(destination, relativePath)));
        Assert.Equal("long path", await File.ReadAllTextAsync(Path.Combine(destination, relativePath)));
    }

    private static async Task<ExtractionResult> ExtractAsync(
        string archivePath,
        string destination,
        ExtractionOptions? options = null,
        ExtractionDestinationMode destinationMode = ExtractionDestinationMode.Auto)
    {
        return await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(archivePath, destination, options, destinationMode));
    }

    private async Task<string> PrepareExistingFile()
    {
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "hello.txt"), "old");
        return destination;
    }

    private string CreateHelloZip()
    {
        return ZipFactory.Create(Path.Combine(_root, "archives"), "hello.zip", ("hello.txt", "new"));
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;

        public InlineProgress(Action<T> action)
        {
            _action = action;
        }

        public void Report(T value) => _action(value);
    }
}
