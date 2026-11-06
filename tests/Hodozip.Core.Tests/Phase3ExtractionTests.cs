using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace Hodozip.Core.Tests;

/// <summary>Phase 3 で追加した多形式解凍・上書き・フォルダ統合のテスト。</summary>
public sealed class Phase3ExtractionTests : IDisposable
{
    private const string Password = "secret";

    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "formats");

    private readonly string _root;

    static Phase3ExtractionTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Phase3ExtractionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HodozipPhase3Tests", Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData("sample.7z")]
    [InlineData("sample.rar")]
    [InlineData("sample.tar")]
    [InlineData("sample.tar.gz")]
    [InlineData("sample.tar.bz2")]
    [InlineData("sample.tar.xz")]
    [InlineData("sample.tar.zst")]
    [InlineData("sample.tgz")]
    [InlineData("sample.tbz2")]
    [InlineData("sample.txz")]
    [InlineData("sample.tzst")]
    public async Task ArchiveFormats_ExtractExpectedContent(string fixtureName)
    {
        var destination = Path.Combine(_root, fixtureName.Replace('.', '_'));

        var result = await ExtractAsync(Fixture(fixtureName), destination, new ExtractionOptions
        {
            OverwritePolicy = OverwritePolicy.Overwrite,
            FolderCollisionPolicy = FolderCollisionPolicy.Merge,
            PasswordProvider = (_, _) => Task.FromResult<string?>(Password),
        });

        Assert.True(result.Success, Describe(result));

        if (fixtureName == "sample.rar")
        {
            // SharpCompress の RAR テストアーカイブには実行ファイルと画像が入っている。
            Assert.True(File.Exists(Path.Combine(result.DestinationPath, "exe", "test.exe")));
            Assert.Equal(45056, new FileInfo(Path.Combine(result.DestinationPath, "exe", "test.exe")).Length);
            Assert.True(File.Exists(Path.Combine(result.DestinationPath, "jpg", "test.jpg")));
            return;
        }

        var root = result.DestinationPath;
        Assert.Equal("hello world", await File.ReadAllTextAsync(Path.Combine(root, "hello.txt")));
        Assert.Equal("A content", await File.ReadAllTextAsync(Path.Combine(root, "Top", "a.txt")));
        Assert.Equal("B content", await File.ReadAllTextAsync(Path.Combine(root, "Top", "sub", "b.txt")));
        Assert.Equal("こんにちは", await File.ReadAllTextAsync(Path.Combine(root, "日本語テスト.txt")));
    }

    [Fact]
    public async Task GZip_ExtractsInnerFile()
    {
        var destination = Path.Combine(_root, "gz");

        var result = await ExtractAsync(Fixture("sample.gz"), destination);

        Assert.True(result.Success, Describe(result));
        Assert.Equal("hello world", await File.ReadAllTextAsync(Path.Combine(destination, "hello.txt")));
    }

    [Theory]
    [InlineData("sample.bz2", "sample")]
    [InlineData("sample.xz", "sample")]
    [InlineData("sample.zst", "sample")]
    public async Task BareCompressed_ExtractsSingleFile(string fixtureName, string expectedName)
    {
        var destination = Path.Combine(_root, fixtureName.Replace('.', '_'));

        var result = await ExtractAsync(Fixture(fixtureName), destination);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(1, result.ExtractedFileCount);
        Assert.Equal("hello world", await File.ReadAllTextAsync(Path.Combine(destination, expectedName)));
    }

    [Fact]
    public async Task Password7z_ExtractsWithCorrectPassword()
    {
        var destination = Path.Combine(_root, "password7z");

        var result = await ExtractAsync(Fixture("password.7z"), destination, new ExtractionOptions
        {
            PasswordProvider = (_, _) => Task.FromResult<string?>(Password),
        });

        Assert.True(result.Success, Describe(result));
        Assert.Equal(
            "Hodozip 7z password test",
            await File.ReadAllTextAsync(Path.Combine(destination, "secret.txt")));
    }

    [Fact]
    public async Task Password7z_WrongPasswordCanRetry()
    {
        var destination = Path.Combine(_root, "password7z-retry");
        var attempts = 0;

        var result = await ExtractAsync(Fixture("password.7z"), destination, new ExtractionOptions
        {
            PasswordProvider = (_, _) =>
            {
                attempts++;
                return Task.FromResult<string?>(attempts == 1 ? "wrong" : Password);
            },
        });

        Assert.True(result.Success, Describe(result));
        Assert.Equal(2, attempts);
        Assert.Equal(
            "Hodozip 7z password test",
            await File.ReadAllTextAsync(Path.Combine(destination, "secret.txt")));
    }

    [Fact]
    public async Task Password7z_Cancelled_ReturnsCancelled()
    {
        var destination = Path.Combine(_root, "password7z-cancel");

        var result = await ExtractAsync(Fixture("password.7z"), destination, new ExtractionOptions
        {
            PasswordProvider = (_, _) => Task.FromResult<string?>(null),
        });

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(destination, "secret.txt")));
    }

    [Fact]
    public async Task TarPathTraversal_IsRejected()
    {
        var parent = Path.Combine(_root, "slip");
        var destination = Path.Combine(parent, "dest");
        var archive = Path.Combine(parent, "slip.tar");
        Directory.CreateDirectory(parent);

        using (var stream = File.Create(archive))
        using (var writer = WriterFactory.OpenWriter(stream, ArchiveType.Tar, new WriterOptions(CompressionType.None)))
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("evil"));
            writer.Write("../evil.txt", content);
        }

        var result = await ExtractAsync(archive, destination);

        Assert.False(result.Success);
        Assert.Contains(result.Issues, x => x.Kind == ExtractionIssueKind.PathTraversal);
        Assert.False(File.Exists(Path.Combine(parent, "evil.txt")));
    }

    [Fact]
    public async Task FolderCollision_Merge_WritesIntoExistingFolder()
    {
        var destination = Path.Combine(_root, "merge");
        Directory.CreateDirectory(Path.Combine(destination, "Top"));
        await File.WriteAllTextAsync(Path.Combine(destination, "Top", "existing.txt"), "existing");

        var result = await ExtractAsync(CreateFolderZip(), destination, new ExtractionOptions
        {
            FolderCollisionPolicy = FolderCollisionPolicy.Merge,
        });

        Assert.True(result.Success, Describe(result));
        Assert.Equal(Path.Combine(destination, "Top"), result.DestinationPath);
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(destination, "Top", "a.txt")));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destination, "Top", "existing.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "Top (2)")));
    }

    [Fact]
    public async Task FolderCollision_AutoRename_CreatesNumberedFolder()
    {
        var destination = Path.Combine(_root, "rename");
        Directory.CreateDirectory(Path.Combine(destination, "Top"));

        var result = await ExtractAsync(CreateFolderZip(), destination, new ExtractionOptions
        {
            FolderCollisionPolicy = FolderCollisionPolicy.AutoRename,
        });

        Assert.True(result.Success, Describe(result));
        Assert.Equal(Path.Combine(destination, "Top (2)"), result.DestinationPath);
        Assert.Equal("A", await File.ReadAllTextAsync(Path.Combine(destination, "Top (2)", "a.txt")));
    }

    [Fact]
    public async Task FolderCollision_Ask_ResolverReceivesConflictAndMerges()
    {
        var destination = Path.Combine(_root, "ask-merge");
        Directory.CreateDirectory(Path.Combine(destination, "Top"));
        FolderCollisionConflict? conflict = null;

        var result = await ExtractAsync(CreateFolderZip(), destination, new ExtractionOptions
        {
            FolderCollisionPolicy = FolderCollisionPolicy.Ask,
            FolderCollisionResolver = (value, _) =>
            {
                conflict = value;
                return Task.FromResult(FolderCollisionDecision.Merge);
            },
        });

        Assert.True(result.Success, Describe(result));
        Assert.NotNull(conflict);
        Assert.Equal("Top", conflict!.FolderName);
        Assert.True(conflict.ExistingIsDirectory);
        Assert.Equal(Path.Combine(destination, "Top"), result.DestinationPath);
        Assert.True(File.Exists(Path.Combine(destination, "Top", "a.txt")));
    }

    [Fact]
    public async Task FolderCollision_Ask_ResolverCanCancel()
    {
        var destination = Path.Combine(_root, "ask-cancel");
        Directory.CreateDirectory(Path.Combine(destination, "Top"));
        var called = false;

        var result = await ExtractAsync(CreateFolderZip(), destination, new ExtractionOptions
        {
            FolderCollisionPolicy = FolderCollisionPolicy.Ask,
            FolderCollisionResolver = (_, _) =>
            {
                called = true;
                return Task.FromResult(FolderCollisionDecision.Cancel);
            },
        });

        Assert.True(called);
        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(destination, "Top", "a.txt")));
    }

    [Fact]
    public async Task FolderCollision_Ask_WithoutResolver_FallsBackToAutoRename()
    {
        var destination = Path.Combine(_root, "ask-default");
        Directory.CreateDirectory(Path.Combine(destination, "Top"));

        var result = await ExtractAsync(CreateFolderZip(), destination);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(Path.Combine(destination, "Top (2)"), result.DestinationPath);
    }

    [Fact]
    public async Task OverwriteCancel_StopsWholeExtraction()
    {
        var destination = Path.Combine(_root, "overwrite-cancel");
        Directory.CreateDirectory(Path.Combine(destination, "Top"));
        await File.WriteAllTextAsync(Path.Combine(destination, "Top", "a.txt"), "old");

        var archive = CreateFolderZip();

        var result = await ExtractAsync(archive, destination, new ExtractionOptions
        {
            OverwritePolicy = OverwritePolicy.Ask,
            FolderCollisionPolicy = FolderCollisionPolicy.Merge,
            OverwriteResolver = (_, _) => Task.FromResult(OverwriteDecision.Cancel),
        });

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(destination, "Top", "a.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "Top", "sub", "b.txt")));
    }

    [Fact]
    public async Task OverwriteAsk_ReceivesSizeAndTimestampComparison()
    {
        var destination = Path.Combine(_root, "overwrite-compare");
        Directory.CreateDirectory(destination);
        var existingPath = Path.Combine(destination, "a.txt");
        await File.WriteAllTextAsync(existingPath, "old");
        var existingTime = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Local);
        File.SetLastWriteTime(existingPath, existingTime);

        var conflict = default(OverwriteConflict);
        var archive = ZipFactory.Create(Path.Combine(_root, "archives"), "compare.zip", ("a.txt", "new-content"));

        var result = await ExtractAsync(archive, destination, new ExtractionOptions
        {
            OverwritePolicy = OverwritePolicy.Ask,
            OverwriteResolver = (value, _) =>
            {
                conflict = value;
                return Task.FromResult(OverwriteDecision.Skip);
            },
        });

        Assert.True(result.Success, Describe(result));
        Assert.NotNull(conflict);
        Assert.Equal(3, conflict!.ExistingSize);
        Assert.Equal(11, conflict.NewSize);
        Assert.NotNull(conflict.ExistingLastWriteTime);
        Assert.NotNull(conflict.NewLastWriteTime);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.Equal("old", await File.ReadAllTextAsync(existingPath));
    }

    [Fact]
    public async Task Progress_ReportsPerFileAndOverallBytes()
    {
        var destination = Path.Combine(_root, "progress");
        var reports = new List<ExtractionProgress>();

        var result = await ExtractAsync(
            Fixture("sample.tar"),
            destination,
            new ExtractionOptions
            {
                OverwritePolicy = OverwritePolicy.Overwrite,
                FolderCollisionPolicy = FolderCollisionPolicy.Merge,
                Progress = new InlineProgress<ExtractionProgress>(reports.Add),
            });

        Assert.True(result.Success, Describe(result));
        Assert.Contains(reports, x => x.CurrentFile == "hello.txt" && x.CurrentFileTotalBytes == 11);
        Assert.Contains(reports, x => x.CurrentFile == "hello.txt" && x.CurrentFileProcessedBytes == 11);
        Assert.Contains(reports, x => x.TotalBytes > 0 && x.ProcessedBytes == x.TotalBytes);
        Assert.Contains(reports, x => string.IsNullOrEmpty(x.CurrentFile));
    }

    [Fact]
    public void SupportedExtensions_AreReported()
    {
        var extensions = ArchiveFormats.SupportedExtensions;

        Assert.Contains(".zip", extensions);
        Assert.Contains(".7z", extensions);
        Assert.Contains(".rar", extensions);
        Assert.Contains(".tar", extensions);
        Assert.Contains(".tar.gz", extensions);
        Assert.Contains(".tgz", extensions);
        Assert.Contains(".tar.bz2", extensions);
        Assert.Contains(".tbz2", extensions);
        Assert.Contains(".tar.xz", extensions);
        Assert.Contains(".txz", extensions);
        Assert.Contains(".tar.zst", extensions);
        Assert.Contains(".tzst", extensions);
        Assert.Contains(".gz", extensions);
        Assert.Contains(".bz2", extensions);
        Assert.Contains(".xz", extensions);
        Assert.Contains(".zst", extensions);
        Assert.DoesNotContain(".cab", extensions);
        Assert.DoesNotContain(".lzh", extensions);
    }

    [Theory]
    [InlineData("archive.tar.gz", "archive")]
    [InlineData("archive.tgz", "archive")]
    [InlineData("archive.tar.bz2", "archive")]
    [InlineData("archive.tbz2", "archive")]
    [InlineData("archive.tar.xz", "archive")]
    [InlineData("archive.txz", "archive")]
    [InlineData("archive.tar.zst", "archive")]
    [InlineData("archive.tzst", "archive")]
    [InlineData("archive.7z", "archive")]
    [InlineData("archive.rar", "archive")]
    public void GetArchiveBaseName_StripsCompoundExtensions(string fileName, string expected)
    {
        Assert.Equal(expected, ArchiveFormats.GetArchiveBaseName(fileName));
    }

    private string Fixture(string name) => Path.Combine(FixtureDirectory, name);

    private string CreateFolderZip()
    {
        return ZipFactory.Create(
            Path.Combine(_root, "archives"),
            "folder.zip",
            ("Top/a.txt", "A"),
            ("Top/sub/b.txt", "B"));
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

    private static string Describe(ExtractionResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Issues.Select(x => $"{x.Severity} {x.Kind} {x.EntryPath}: {x.Message}"));
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
