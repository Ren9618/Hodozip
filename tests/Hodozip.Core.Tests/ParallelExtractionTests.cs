using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Hodozip.Core.Tests;

/// <summary>Phase 4 の ZIP 並列解凍と逐次解凍の等価性テスト。</summary>
public sealed class ParallelExtractionTests : IDisposable
{
    private readonly string _root;

    static ParallelExtractionTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ParallelExtractionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HodozipParallelTests", Guid.NewGuid().ToString("N"));
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
    public async Task Parallel_OutputMatchesSequential()
    {
        var archive = CreateRepresentativeZip();
        var sequential = await ExtractAsync(archive, Path.Combine(_root, "seq"), maxDegreeOfParallelism: 1);
        var parallel = await ExtractAsync(archive, Path.Combine(_root, "par"), maxDegreeOfParallelism: 4);

        Assert.True(sequential.Success, Describe(sequential));
        Assert.True(parallel.Success, Describe(parallel));
        Assert.Equal(sequential.ExtractedFileCount, parallel.ExtractedFileCount);
        Assert.Equal(sequential.SkippedFileCount, parallel.SkippedFileCount);
        AssertSameTree(sequential.DestinationPath, parallel.DestinationPath);
    }

    [Fact]
    public async Task Parallel_ShiftJisFileNames_MatchSequential()
    {
        var archive = CreateShiftJisZip();
        var sequential = await ExtractAsync(archive, Path.Combine(_root, "seq-sjis"), maxDegreeOfParallelism: 1);
        var parallel = await ExtractAsync(archive, Path.Combine(_root, "par-sjis"), maxDegreeOfParallelism: 4);

        Assert.True(sequential.Success, Describe(sequential));
        Assert.True(parallel.Success, Describe(parallel));
        AssertSameTree(sequential.DestinationPath, parallel.DestinationPath);
        Assert.True(File.Exists(Path.Combine(parallel.DestinationPath, "日本語フォルダ", "テスト.txt")));
    }

    [Fact]
    public async Task Parallel_MergePolicy_WritesIntoExistingFolder()
    {
        var archive = CreateTopFolderZip();

        var sequentialBase = Path.Combine(_root, "merge-seq");
        var parallelBase = Path.Combine(_root, "merge-par");
        Directory.CreateDirectory(Path.Combine(sequentialBase, "Top"));
        Directory.CreateDirectory(Path.Combine(parallelBase, "Top"));
        await File.WriteAllTextAsync(Path.Combine(sequentialBase, "Top", "existing.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(parallelBase, "Top", "existing.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(sequentialBase, "Top", "a.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(parallelBase, "Top", "a.txt"), "old");

        var options = new ExtractionOptions
        {
            FolderCollisionPolicy = FolderCollisionPolicy.Merge,
            OverwritePolicy = OverwritePolicy.Overwrite,
        };

        var sequential = await ExtractAsync(archive, sequentialBase, 1, options);
        var parallel = await ExtractAsync(archive, parallelBase, 4, options);

        Assert.True(sequential.Success, Describe(sequential));
        Assert.True(parallel.Success, Describe(parallel));
        Assert.Equal(Path.Combine(parallelBase, "Top"), parallel.DestinationPath);
        Assert.Equal("new-a", await File.ReadAllTextAsync(Path.Combine(parallelBase, "Top", "a.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(parallelBase, "Top", "existing.txt")));
        AssertSameTree(sequential.DestinationPath, parallel.DestinationPath);
    }

    [Fact]
    public async Task Parallel_AskResolverRunsBeforeAnyNewFileIsWritten()
    {
        var archive = CreateTopFolderZip();
        var destination = Path.Combine(_root, "ask");
        Directory.CreateDirectory(Path.Combine(destination, "Top"));
        await File.WriteAllTextAsync(Path.Combine(destination, "Top", "a.txt"), "old");

        var resolverCalls = 0;
        var newFilesVisibleDuringResolve = new List<bool>();

        var result = await ExtractAsync(archive, destination, 4, new ExtractionOptions
        {
            FolderCollisionPolicy = FolderCollisionPolicy.Merge,
            OverwritePolicy = OverwritePolicy.Ask,
            OverwriteResolver = (conflict, _) =>
            {
                resolverCalls++;
                newFilesVisibleDuringResolve.Add(
                    File.Exists(Path.Combine(destination, "Top", "b.txt"))
                    || File.Exists(Path.Combine(destination, "Top", "sub", "c.txt")));
                return Task.FromResult(OverwriteDecision.Overwrite);
            },
        });

        Assert.True(result.Success, Describe(result));
        Assert.Equal(1, resolverCalls);
        Assert.All(newFilesVisibleDuringResolve, visible => Assert.False(visible));
        Assert.Equal("new-a", await File.ReadAllTextAsync(Path.Combine(destination, "Top", "a.txt")));
        Assert.Equal("new-b", await File.ReadAllTextAsync(Path.Combine(destination, "Top", "b.txt")));
    }

    [Fact]
    public async Task Parallel_PathTraversal_IsRejectedAndSafeEntriesExtracted()
    {
        var archive = CreateZip("traversal.zip",
            ("../evil.txt", Encoding.UTF8.GetBytes("evil")),
            ("safe.txt", Encoding.UTF8.GetBytes("safe")),
            ("Top/inside.txt", Encoding.UTF8.GetBytes("inside")));

        var destination = Path.Combine(_root, "traversal");
        var result = await ExtractAsync(archive, destination, 4);

        Assert.Contains(result.Issues, x => x.Kind == ExtractionIssueKind.PathTraversal);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
        Assert.Equal("safe", await File.ReadAllTextAsync(Path.Combine(result.DestinationPath, "safe.txt")));
        Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(result.DestinationPath, "Top", "inside.txt")));
    }

    [Fact]
    public async Task Parallel_Cancellation_IsConsistentAndLeavesNoTemporaryFiles()
    {
        var archive = CreateZip("cancel.zip",
            ("large.bin", CreateDeterministicBytes(16 * 1024 * 1024)),
            ("second.bin", CreateDeterministicBytes(4 * 1024 * 1024)),
            ("third.bin", CreateDeterministicBytes(4 * 1024 * 1024)));

        using var cancellation = new CancellationTokenSource();
        var progress = new CancellingProgress(cancellation);

        var result = await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(
                archive,
                Path.Combine(_root, "cancel"),
                new ExtractionOptions
                {
                    MaxDegreeOfParallelism = 4,
                    Progress = progress,
                },
                ExtractionDestinationMode.ForceArchiveFolder),
            cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Contains(result.Issues, x => x.Kind == ExtractionIssueKind.Cancelled);
        Assert.Empty(Directory.EnumerateFiles(result.DestinationPath, "~hodozip-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SequentialFallback_MaxDegreeOfParallelismOne_ProducesSameOutput()
    {
        var archive = CreateRepresentativeZip();
        var result = await ExtractAsync(archive, Path.Combine(_root, "fallback"), maxDegreeOfParallelism: 1);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(27, result.ExtractedFileCount);
    }

    private string CreateRepresentativeZip()
    {
        var entries = new List<(string Name, byte[] Data)>
        {
            ("README.txt", Encoding.UTF8.GetBytes("Hodozip parallel test")),
            ("日本語テスト.txt", Encoding.UTF8.GetBytes("こんにちは")),
        };

        for (var folder = 0; folder < 4; folder++)
        {
            for (var file = 0; file < 6; file++)
            {
                entries.Add((
                    $"Folder{folder}/file{file}.txt",
                    Encoding.UTF8.GetBytes($"folder={folder};file={file};")));
            }
        }

        entries.Add(("Large/large.bin", CreateDeterministicBytes(2 * 1024 * 1024)));
        return CreateZip("representative.zip", entries.ToArray());
    }

    private string CreateTopFolderZip()
    {
        return CreateZip(
            "top.zip",
            ("Top/a.txt", Encoding.UTF8.GetBytes("new-a")),
            ("Top/b.txt", Encoding.UTF8.GetBytes("new-b")),
            ("Top/sub/c.txt", Encoding.UTF8.GetBytes("new-c")),
            ("Top/sub/d.txt", Encoding.UTF8.GetBytes("new-d")));
    }

    private string CreateShiftJisZip()
    {
        var zip = new TestZip();
        zip.AddStored("日本語フォルダ\\テスト.txt", "Shift_JIS content", Encoding.GetEncoding(932));
        zip.AddStored("日本語フォルダ\\その他.txt", "another", Encoding.GetEncoding(932));
        zip.AddStored("ascii.txt", "ascii", Encoding.UTF8);
        var path = Path.Combine(_root, "shiftjis.zip");
        File.WriteAllBytes(path, zip.Build());
        return path;
    }

    private string CreateZip(string fileName, params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(_root, fileName);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            entryStream.Write(data);
        }

        return path;
    }

    private static async Task<ExtractionResult> ExtractAsync(
        string archivePath,
        string destination,
        int maxDegreeOfParallelism,
        ExtractionOptions? options = null)
    {
        options ??= new ExtractionOptions();
        var merged = new ExtractionOptions
        {
            OverwritePolicy = options.OverwritePolicy,
            OverwriteResolver = options.OverwriteResolver,
            FolderCollisionPolicy = options.FolderCollisionPolicy,
            FolderCollisionResolver = options.FolderCollisionResolver,
            PasswordProvider = options.PasswordProvider,
            Progress = options.Progress,
            ExpansionWarningMinBytes = options.ExpansionWarningMinBytes,
            ExpansionWarningRatio = options.ExpansionWarningRatio,
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
        };

        return await new SharpCompressExtractor().ExtractAsync(
            new ExtractionRequest(archivePath, destination, merged));
    }

    private static void AssertSameTree(string expectedRoot, string actualRoot)
    {
        var expected = EnumerateTree(expectedRoot);
        var actual = EnumerateTree(actualRoot);
        Assert.Equal(expected.Keys.OrderBy(x => x, StringComparer.Ordinal), actual.Keys.OrderBy(x => x, StringComparer.Ordinal));

        foreach (var (relativePath, expectedHash) in expected)
        {
            Assert.Equal(expectedHash, actual[relativePath]);
        }
    }

    private static Dictionary<string, string> EnumerateTree(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            result[relative] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        }

        return result;
    }

    private static byte[] CreateDeterministicBytes(int length)
    {
        var data = new byte[length];
        var random = new Random(12345);
        random.NextBytes(data);
        return data;
    }

    private static string Describe(ExtractionResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Issues.Select(x => $"{x.Severity} {x.Kind} {x.EntryPath}: {x.Message}"));
    }

    private sealed class CancellingProgress : IProgress<ExtractionProgress>
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingProgress(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public void Report(ExtractionProgress value)
        {
            if (!string.IsNullOrEmpty(value.CurrentFile) && value.CurrentFileProcessedBytes == 0)
            {
                _cancellation.Cancel();
            }
        }
    }
}
