using System.Text;

namespace Hodozip.Core.Tests;

public sealed class CompressionTests : IDisposable
{
    private readonly string _root;

    public CompressionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HodozipCompressionTests", Guid.NewGuid().ToString("N"));
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
    public async Task Zip_RoundTripsFilesAndFolders()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "sample.zip");

        var result = await CompressAsync(new[] { source }, output, ArchiveCreationFormat.Zip);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.FileCount);
        Assert.True(File.Exists(output));

        var destination = Path.Combine(_root, "extracted-zip");
        var extraction = await ExtractAsync(output, destination);
        Assert.True(extraction.Success);

        Assert.Equal("alpha", await File.ReadAllTextAsync(
            Path.Combine(destination, "Sample", "alpha.txt")));
        Assert.Equal("beta", await File.ReadAllTextAsync(
            Path.Combine(destination, "Sample", "sub", "beta.txt")));
        Assert.Equal("日本語", await File.ReadAllTextAsync(
            Path.Combine(destination, "Sample", "sub", "日本語.txt")));
    }

    [Fact]
    public async Task SevenZip_RoundTripsFilesAndFolders()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "sample.7z");

        var result = await CompressAsync(new[] { source }, output, ArchiveCreationFormat.SevenZip);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(output));

        var destination = Path.Combine(_root, "extracted-7z");
        var extraction = await ExtractAsync(output, destination);
        Assert.True(extraction.Success);
        Assert.Equal("alpha", await File.ReadAllTextAsync(
            Path.Combine(destination, "Sample", "alpha.txt")));
        Assert.Equal("日本語", await File.ReadAllTextAsync(
            Path.Combine(destination, "Sample", "sub", "日本語.txt")));
    }

    [Fact]
    public async Task Zip_StoreLevel_RoundTripsAndIsLargerThanNormal()
    {
        var source = Path.Combine(_root, "repetitive");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "data.txt"), new string('A', 200_000));

        var store = Path.Combine(_root, "store.zip");
        var normal = Path.Combine(_root, "normal.zip");

        var storeResult = await CompressAsync(
            new[] { source }, store, ArchiveCreationFormat.Zip, ArchiveCompressionLevel.Store);
        var normalResult = await CompressAsync(
            new[] { source }, normal, ArchiveCreationFormat.Zip, ArchiveCompressionLevel.Normal);

        Assert.True(storeResult.Success, storeResult.ErrorMessage);
        Assert.True(normalResult.Success, normalResult.ErrorMessage);
        Assert.True(new FileInfo(store).Length > new FileInfo(normal).Length);

        var extraction = await ExtractAsync(store, Path.Combine(_root, "extracted-store"));
        Assert.True(extraction.Success);
        Assert.Equal(200_000, new FileInfo(Path.Combine(
            _root, "extracted-store", "repetitive", "data.txt")).Length);
    }

    [Fact]
    public async Task Zip_MaximumLevel_RoundTrips()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "max.zip");

        var result = await CompressAsync(
            new[] { source }, output, ArchiveCreationFormat.Zip, ArchiveCompressionLevel.Maximum);
        Assert.True(result.Success, result.ErrorMessage);

        var extraction = await ExtractAsync(output, Path.Combine(_root, "extracted-max"));
        Assert.True(extraction.Success);
        Assert.Equal("alpha", await File.ReadAllTextAsync(
            Path.Combine(_root, "extracted-max", "Sample", "alpha.txt")));
    }

    [Fact]
    public async Task Zip_Utf8Names_HaveUtf8Flag()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "utf8.zip");

        var result = await CompressAsync(
            new[] { source },
            output,
            ArchiveCreationFormat.Zip,
            zipNameEncoding: ZipNameEncoding.Utf8);
        Assert.True(result.Success, result.ErrorMessage);

        var bytes = await File.ReadAllBytesAsync(output);
        Assert.True((BitConverter.ToUInt16(bytes, 6) & 0x0800) != 0);
        Assert.True(ContainsSequence(bytes, Encoding.UTF8.GetBytes("日本語.txt")));
    }

    [Fact]
    public async Task Zip_ShiftJisNames_HaveNoUtf8FlagAndCp932Bytes()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "sjis.zip");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var result = await CompressAsync(
            new[] { source },
            output,
            ArchiveCreationFormat.Zip,
            zipNameEncoding: ZipNameEncoding.ShiftJis);
        Assert.True(result.Success, result.ErrorMessage);

        var bytes = await File.ReadAllBytesAsync(output);
        Assert.True((BitConverter.ToUInt16(bytes, 6) & 0x0800) == 0);
        Assert.True(ContainsSequence(bytes, Encoding.GetEncoding(932).GetBytes("日本語.txt")));
    }

    [Fact]
    public async Task Zip_AesPassword_RoundTripsThroughHodozip()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "password.zip");

        var result = await CompressAsync(
            new[] { source },
            output,
            ArchiveCreationFormat.Zip,
            password: "Passw0rd!");
        Assert.True(result.Success, result.ErrorMessage);

        var destination = Path.Combine(_root, "extracted-password");
        var extraction = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(
                output,
                destination,
                new ExtractionOptions
                {
                    PasswordProvider = (_, _) => Task.FromResult<string?>("Passw0rd!"),
                }));

        Assert.True(extraction.Success, Describe(extraction));
        Assert.Equal("alpha", await File.ReadAllTextAsync(
            Path.Combine(destination, "Sample", "alpha.txt")));
    }

    [Fact]
    public async Task Zip_AesPassword_WithShiftJisNames_RoundTrips()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "password-sjis.zip");

        var result = await CompressAsync(
            new[] { source },
            output,
            ArchiveCreationFormat.Zip,
            password: "Passw0rd!",
            zipNameEncoding: ZipNameEncoding.ShiftJis);
        Assert.True(result.Success, result.ErrorMessage);

        var destination = Path.Combine(_root, "extracted-password-sjis");
        var extraction = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(
                output,
                destination,
                new ExtractionOptions
                {
                    PasswordProvider = (_, _) => Task.FromResult<string?>("Passw0rd!"),
                }));

        Assert.True(extraction.Success, Describe(extraction));
        Assert.True(File.Exists(Path.Combine(destination, "Sample", "sub", "日本語.txt")));
    }

    private static string Describe(ExtractionResult result)
    {
        return string.Join(" | ", result.Issues.Select(x => $"{x.Kind}: {x.Message}"));
    }

    [Fact]
    public async Task SevenZip_Password_IsIgnored()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "ignored-password.7z");

        var result = await CompressAsync(
            new[] { source },
            output,
            ArchiveCreationFormat.SevenZip,
            password: "secret");
        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.PasswordIgnored);

        var extraction = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(output, Path.Combine(_root, "extracted-7z-password")));
        Assert.True(extraction.Success);
    }

    [Fact]
    public async Task Cancellation_LeavesNoPartialArchive()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "cancelled.zip");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new SharpCompressCompressor().CompressAsync(
            new CompressionRequest(new[] { source }, output),
            cancellation.Token);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_root, "cancelled.zip.tmp-*"));
    }

    [Fact]
    public async Task ExistingOutput_GetsNumberedSuffix()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "collision.zip");
        await File.WriteAllTextAsync(output, "existing");

        var result = await CompressAsync(new[] { source }, output, ArchiveCreationFormat.Zip);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("existing", await File.ReadAllTextAsync(output));
        Assert.Equal(Path.Combine(_root, "collision (2).zip"), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public void DefaultOutputPath_UsesItemName()
    {
        var folder = Path.Combine(_root, "MyFolder");
        Directory.CreateDirectory(folder);

        Assert.Equal(
            Path.Combine(_root, "MyFolder.zip"),
            CompressionDefaults.GetDefaultOutputPath(new[] { folder }, ArchiveCreationFormat.Zip));

        var file = Path.Combine(_root, "notes.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(
            Path.Combine(_root, "notes.7z"),
            CompressionDefaults.GetDefaultOutputPath(new[] { file }, ArchiveCreationFormat.SevenZip));
    }

    [Fact]
    public async Task MultipleSources_KeepEachTopLevelName()
    {
        var first = Path.Combine(_root, "one");
        var second = Path.Combine(_root, "two");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        await File.WriteAllTextAsync(Path.Combine(first, "a.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(second, "b.txt"), "2");

        var output = Path.Combine(_root, "multiple.zip");
        var result = await CompressAsync(
            new[] { first, second }, output, ArchiveCreationFormat.Zip);
        Assert.True(result.Success, result.ErrorMessage);

        var destination = Path.Combine(_root, "extracted-multiple");
        var extraction = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(
                output,
                destination,
                destinationMode: ExtractionDestinationMode.ForceArchiveFolder));
        Assert.True(extraction.Success);
        Assert.Equal("1", await File.ReadAllTextAsync(
            Path.Combine(destination, "multiple", "one", "a.txt")));
        Assert.Equal("2", await File.ReadAllTextAsync(
            Path.Combine(destination, "multiple", "two", "b.txt")));
    }

    [Fact]
    public async Task Progress_ReportsTotalAndCompletion()
    {
        var source = await CreateSampleTree();
        var output = Path.Combine(_root, "progress.zip");
        var reports = new List<CompressionProgress>();

        var result = await new SharpCompressCompressor().CompressAsync(
            new CompressionRequest(
                new[] { source },
                output,
                new CompressionOptions
                {
                    Progress = new InlineProgress<CompressionProgress>(reports.Add),
                }));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(reports);
        Assert.All(reports, x => Assert.Equal(result.SourceBytes, x.TotalBytes));
        Assert.Equal(result.SourceBytes, reports[^1].ProcessedBytes);
    }

    private async Task<CompressionResult> CompressAsync(
        IReadOnlyList<string> sources,
        string output,
        ArchiveCreationFormat format,
        ArchiveCompressionLevel level = ArchiveCompressionLevel.Normal,
        string? password = null,
        ZipNameEncoding zipNameEncoding = ZipNameEncoding.Utf8)
    {
        return await new SharpCompressCompressor().CompressAsync(
            new CompressionRequest(
                sources,
                output,
                new CompressionOptions
                {
                    Format = format,
                    Level = level,
                    Password = password,
                    ZipNameEncoding = zipNameEncoding,
                }));
    }

    private static async Task<ExtractionResult> ExtractAsync(string archive, string destination)
    {
        return await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(archive, destination));
    }

    private async Task<string> CreateSampleTree()
    {
        var root = Path.Combine(_root, "Sample");
        Directory.CreateDirectory(Path.Combine(root, "sub", "empty"));
        await File.WriteAllTextAsync(Path.Combine(root, "alpha.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "beta.txt"), "beta");
        await File.WriteAllTextAsync(Path.Combine(root, "sub", "日本語.txt"), "日本語");
        return root;
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle) >= 0;
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
