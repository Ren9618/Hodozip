using System.Text;

namespace Hodozip.Core.Tests;

public sealed class ZipFileNameEncodingTests : IDisposable
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "encoding");
    private readonly string _root;

    private static readonly string[] ExpectedFixtureNames =
    {
        "HelloWorld.txt",
        "©®™★♦♣♥♠€£￥✓♫.txt",
        "こんにちは世界.txt",
        "你好世界.txt",
    };

    static ZipFileNameEncodingTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ZipFileNameEncodingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HodozipEncodingTests", Guid.NewGuid().ToString("N"));
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
    public void Auto_RescuesUtf8WithoutFlag()
    {
        const string expected = "フラグなし日本語.txt";
        var raw = Encoding.UTF8.GetBytes(expected);

        Assert.Equal(ZipFileNameEncoding.Utf8, ZipFileNameDecoder.DetectEncoding(raw));
        Assert.Equal(expected, ZipFileNameDecoder.DecodeEntryName(raw, 0, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.Auto));
    }

    [Fact]
    public void Auto_DetectsCp932()
    {
        const string expected = "日本語テスト.txt";
        var cp932 = Encoding.GetEncoding(932);
        var raw = cp932.GetBytes(expected);

        Assert.Equal(ZipFileNameEncoding.ShiftJis, ZipFileNameDecoder.DetectEncoding(raw));
        Assert.Equal(expected, ZipFileNameDecoder.DecodeEntryName(raw, 0, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.Auto));
    }

    [Fact]
    public void Auto_DetectsGbkJapanese()
    {
        const string expected = "こんにちは世界.txt";
        var gbk = Encoding.GetEncoding(936);
        var raw = gbk.GetBytes(expected);

        Assert.Equal(ZipFileNameEncoding.Gbk, ZipFileNameDecoder.DetectEncoding(raw));
        Assert.Equal(expected, ZipFileNameDecoder.DecodeEntryName(raw, 0, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.Auto));
    }

    [Fact]
    public void Auto_DetectsGbkChinese()
    {
        const string expected = "你好世界.txt";
        var gbk = Encoding.GetEncoding(936);
        var raw = gbk.GetBytes(expected);

        Assert.Equal(ZipFileNameEncoding.Gbk, ZipFileNameDecoder.DetectEncoding(raw));
        Assert.Equal(expected, ZipFileNameDecoder.DecodeEntryName(raw, 0, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.Auto));
    }

    [Fact]
    public void Auto_FallsBackToCp932OnTie()
    {
        // 0x81 は UTF-8 として不正で、CP932/GBK のどちらでも置換文字になる。
        // スコアが同点になった場合は CP932（日本語環境の安全側）を選ぶ。
        ReadOnlySpan<byte> raw = stackalloc byte[] { 0x81 };

        Assert.Equal(ZipFileNameEncoding.ShiftJis, ZipFileNameDecoder.DetectEncoding(raw));
        Assert.Equal(
            ZipFileNameDecoder.DecodeEntryName(raw, 0, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.ShiftJis),
            ZipFileNameDecoder.DecodeEntryName(raw, 0, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.Auto));
    }

    [Fact]
    public void Auto_PrefersUnicodePathExtraField()
    {
        var cp932 = Encoding.GetEncoding(932);
        var raw = cp932.GetBytes("日本語.txt");
        var unicodeName = "日本語_unicode.txt";
        var extra = BuildUnicodePathExtra(raw, unicodeName);

        var decoded = ZipFileNameDecoder.DecodeEntryName(raw, 0, extra, ZipFileNameEncoding.Auto);

        Assert.Equal(unicodeName, decoded);
    }

    [Fact]
    public void ManualEncoding_OverridesAutoDetectionForUnflaggedEntry()
    {
        const string expected = "日本語テスト.txt";
        var cp932 = Encoding.GetEncoding(932);
        var raw = cp932.GetBytes(expected);

        Assert.Equal(expected, ZipFileNameDecoder.DecodeEntryName(raw, 0, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.ShiftJis));
        Assert.Equal(
            Encoding.GetEncoding(936).GetString(raw),
            ZipFileNameDecoder.DecodeEntryName(raw, 0, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.Gbk));
        Assert.NotEqual(expected, ZipFileNameDecoder.DecodeEntryName(raw, 0, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.Gbk));
    }

    [Fact]
    public async Task ManualEncoding_AppliesToExtraction()
    {
        var archiveDirectory = Path.Combine(_root, "archives");
        var destination = Path.Combine(_root, "dest");
        Directory.CreateDirectory(archiveDirectory);

        var cp932 = Encoding.GetEncoding(932);
        var rawName = cp932.GetBytes("日本語テスト.txt");
        var manuallyDecodedName = Encoding.GetEncoding(936).GetString(rawName);

        var zip = new TestZip();
        zip.AddStored("日本語テスト.txt", "manual", cp932, utf8Flag: false);
        var archive = Path.Combine(archiveDirectory, "manual.zip");
        await File.WriteAllBytesAsync(archive, zip.Build());

        var result = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(
                archive,
                destination,
                new ExtractionOptions
                {
                    ZipFileNameEncoding = ZipFileNameEncoding.Gbk,
                }));

        Assert.True(result.Success, string.Join(" / ", result.Issues.Select(x => x.Message)));
        Assert.True(File.Exists(Path.Combine(destination, manuallyDecodedName)));
    }

    [Fact]
    public void Utf8Flag_WinsOverManualEncoding()
    {
        const string expected = "UTF-8フラグ付き.txt";
        var raw = Encoding.UTF8.GetBytes(expected);

        Assert.Equal(
            expected,
            ZipFileNameDecoder.DecodeEntryName(raw, 0x0800, ReadOnlySpan<byte>.Empty, ZipFileNameEncoding.Gbk));
    }

    [Fact]
    public void ManualEncoding_OverridesUnicodePathExtraField()
    {
        var cp932 = Encoding.GetEncoding(932);
        var raw = cp932.GetBytes("日本語.txt");
        var extra = BuildUnicodePathExtra(raw, "日本語_unicode.txt");

        var decoded = ZipFileNameDecoder.DecodeEntryName(raw, 0, extra, ZipFileNameEncoding.ShiftJis);

        Assert.Equal("日本語.txt", decoded);
    }

    [Theory]
    [InlineData("new7z_utf8.zip")]
    [InlineData("new7z_cp932.zip")]
    [InlineData("new7z_cp936.zip")]
    [InlineData("new7z_iso8859-1.zip")]
    [InlineData("old7z_utf8.zip")]
    [InlineData("old7z_cp932.zip")]
    [InlineData("old7z_cp936.zip")]
    [InlineData("old7z_iso8859-1.zip")]
    public async Task FixtureZip_ExtractsAllNamesCorrectly(string archiveName)
    {
        var archivePath = Path.Combine(FixtureDirectory, archiveName);
        Assert.True(File.Exists(archivePath), $"フィクスチャが見つかりません: {archivePath}");

        var destination = Path.Combine(_root, Path.GetFileNameWithoutExtension(archiveName));
        var result = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(
                archivePath,
                destination,
                new ExtractionOptions
                {
                    OverwritePolicy = OverwritePolicy.Overwrite,
                    FolderCollisionPolicy = FolderCollisionPolicy.Merge,
                    MaxDegreeOfParallelism = 4,
                }));

        Assert.True(result.Success, string.Join(" / ", result.Issues.Select(x => x.Message)));

        var actual = Directory
            .EnumerateFiles(result.DestinationPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(result.DestinationPath, path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ExpectedFixtureNames.OrderBy(name => name, StringComparer.Ordinal),
            actual);
    }

    private static byte[] BuildUnicodePathExtra(byte[] rawName, string unicodeName)
    {
        var unicodeBytes = Encoding.UTF8.GetBytes(unicodeName);
        var extra = new byte[4 + 1 + 4 + unicodeBytes.Length];
        extra[0] = 0x75;
        extra[1] = 0x70;
        extra[2] = (byte)(1 + 4 + unicodeBytes.Length);
        extra[3] = (byte)((1 + 4 + unicodeBytes.Length) >> 8);
        extra[4] = 1;
        var crc = ComputeCrc32(rawName);
        extra[5] = (byte)crc;
        extra[6] = (byte)(crc >> 8);
        extra[7] = (byte)(crc >> 16);
        extra[8] = (byte)(crc >> 24);
        unicodeBytes.CopyTo(extra, 9);
        return extra;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
