using System.Text;

namespace Hodozip.Core.Tests;

public sealed class PasswordGeneratorTests
{
    [Theory]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(64)]
    public void Generate_UsesRequestedLengthAndCharacterSets(int length)
    {
        var password = PasswordGenerator.Generate(length);

        Assert.Equal(length, password.Length);
        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsDigit);
        Assert.Contains(password, c => "!#$%&()*+,-./:;<=>?@[]^_{|}~".Contains(c));
    }

    [Fact]
    public void Generate_HonorsSelectedSetsOnly()
    {
        var password = PasswordGenerator.Generate(
            24,
            lowerCase: true,
            upperCase: false,
            digits: true,
            symbols: false);

        Assert.Equal(24, password.Length);
        Assert.All(password, c => Assert.True(char.IsLower(c) || char.IsDigit(c), c.ToString()));
        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsDigit);
    }

    [Fact]
    public void Generate_IsRandom()
    {
        var values = Enumerable.Range(0, 20)
            .Select(_ => PasswordGenerator.Generate(20))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(20, values.Count);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(65)]
    public void Generate_RejectsOutOfRangeLength(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(length));
    }

    [Fact]
    public void Generate_RejectsNoCharacterSets()
    {
        Assert.Throws<InvalidOperationException>(() => PasswordGenerator.Generate(
            20,
            lowerCase: false,
            upperCase: false,
            digits: false,
            symbols: false));
    }
}

public sealed class MacOsCompatibilityTests : IDisposable
{
    private readonly string _root;

    public MacOsCompatibilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HodozipPhase6Tests", Guid.NewGuid().ToString("N"));
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
    public async Task Extract_ExcludesMacOsMetadataAndNormalizesNfc()
    {
        var nfdName = "テスト".Normalize(NormalizationForm.FormD);
        var nfcName = nfdName.Normalize(NormalizationForm.FormC);
        var archive = CreateArchive(
            ("docs/" + nfdName + ".txt", "japanese"),
            ("__MACOSX/docs/._" + nfdName + ".txt", "metadata"),
            (".DS_Store", "metadata"),
            ("docs/._resource", "metadata"));

        var destination = Path.Combine(_root, "dest-on");
        var result = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(archive, destination));

        Assert.True(result.Success);
        Assert.Equal(3, result.SkippedFileCount);
        Assert.Equal(1, result.ExtractedFileCount);

        var output = Path.Combine(destination, "docs");
        Assert.True(File.Exists(Path.Combine(output, nfcName + ".txt")));
        Assert.False(File.Exists(Path.Combine(output, "._resource")));
        Assert.False(Directory.Exists(Path.Combine(destination, "__MACOSX")));
        Assert.False(File.Exists(Path.Combine(destination, ".DS_Store")));
    }

    [Fact]
    public async Task Extract_KeepsMacOsMetadataAndNfdNamesWhenDisabled()
    {
        var nfdName = "テスト".Normalize(NormalizationForm.FormD);
        var archive = CreateArchive(
            ("docs/" + nfdName + ".txt", "japanese"),
            ("docs/._resource", "metadata"),
            (".DS_Store", "metadata"));

        var destination = Path.Combine(_root, "dest-off");
        var options = new ExtractionOptions
        {
            ExcludeMacOsMetadata = false,
            NormalizeFileNamesNfc = false,
        };

        var result = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(archive, destination, options));

        Assert.True(result.Success);
        Assert.Equal(3, result.ExtractedFileCount);
        Assert.Equal(0, result.SkippedFileCount);

        var output = Path.Combine(result.DestinationPath, "docs");
        Assert.True(File.Exists(Path.Combine(output, nfdName + ".txt")));
        Assert.True(File.Exists(Path.Combine(output, "._resource")));
        Assert.True(File.Exists(Path.Combine(result.DestinationPath, ".DS_Store")));
    }

    [Fact]
    public async Task Compress_MacOsCompatible_WritesUtf8FlaggedNfcNames()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        var nfdName = "テスト".Normalize(NormalizationForm.FormD);
        var nfcName = nfdName.Normalize(NormalizationForm.FormC);
        await File.WriteAllTextAsync(Path.Combine(source, nfdName + ".txt"), "content");

        var output = Path.Combine(_root, "mac.zip");
        var result = await new SharpCompressCompressor().CompressAsync(
            new CompressionRequest(
                new[] { source },
                output,
                new CompressionOptions
                {
                    Format = ArchiveCreationFormat.Zip,
                    MacOsCompatible = true,
                }));

        Assert.True(result.Success, result.ErrorMessage);

        var entries = ReadCentralDirectory(output);
        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.True(entry.Utf8Flag, entry.Name));
        Assert.All(entries, entry => Assert.Equal(entry.Name.Normalize(NormalizationForm.FormC), entry.Name));
        Assert.Contains(entries, entry => entry.Name.EndsWith(nfcName + ".txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Extract_SkipsExistingNewerFileWithoutAsking()
    {
        var archive = CreateArchive(("older.txt", "from archive"));
        var destination = Path.Combine(_root, "dest-newer");
        Directory.CreateDirectory(destination);

        var existing = Path.Combine(destination, "older.txt");
        await File.WriteAllTextAsync(existing, "local newer");
        File.SetLastWriteTimeUtc(existing, DateTime.UtcNow.AddYears(2));

        var askCount = 0;
        var options = new ExtractionOptions
        {
            OverwritePolicy = OverwritePolicy.Ask,
            SkipExistingIfNewer = true,
            OverwriteResolver = (_, _) =>
            {
                askCount++;
                return Task.FromResult(OverwriteDecision.Overwrite);
            },
        };

        var result = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(archive, destination, options));

        Assert.True(result.Success);
        Assert.Equal(0, askCount);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.Equal("local newer", await File.ReadAllTextAsync(existing));
    }

    private string CreateArchive(params (string Name, string Content)[] entries)
    {
        var zip = new TestZip();
        foreach (var (name, content) in entries)
        {
            zip.AddStored(name, content, utf8Flag: true);
        }

        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(path, zip.Build());
        return path;
    }

    private static List<(string Name, bool Utf8Flag)> ReadCentralDirectory(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var entries = new List<(string, bool)>();

        for (var offset = 0; offset <= bytes.Length - 46; offset++)
        {
            if (BitConverter.ToUInt32(bytes, offset) != 0x02014b50)
            {
                continue;
            }

            var flags = BitConverter.ToUInt16(bytes, offset + 8);
            var nameLength = BitConverter.ToUInt16(bytes, offset + 28);
            var extraLength = BitConverter.ToUInt16(bytes, offset + 30);
            var commentLength = BitConverter.ToUInt16(bytes, offset + 32);
            var name = Encoding.UTF8.GetString(bytes, offset + 46, nameLength);
            entries.Add((name, (flags & 0x0800) != 0));

            offset += 45 + nameLength + extraLength + commentLength;
        }

        return entries;
    }
}
