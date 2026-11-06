using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hodozip.Core;

namespace Hodozip.Perf;

internal static class Program
{
    private const long MiB = 1024L * 1024L;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length >= 2 && string.Equals(args[0], "--compare", StringComparison.OrdinalIgnoreCase))
        {
            return await CompareAsync(args[1], args[2], FindOption(args, "--out"));
        }

        var mode = (FindOption(args, "--mode") ?? "parallel").ToLowerInvariant();
        if (mode is not ("sequential" or "parallel"))
        {
            Console.Error.WriteLine("--mode は sequential または parallel を指定してください。");
            return 2;
        }

        var iterations = ParseInt(FindOption(args, "--iterations"), 2);
        var warmup = ParseInt(FindOption(args, "--warmup"), 1);
        var requestedParallelism = ParseInt(FindOption(args, "--parallelism"), 0);
        var only = FindOption(args, "--only");
        var repoRoot = FindRepositoryRoot();
        var perfRoot = Path.Combine(repoRoot, "artifacts", "perf");
        var fixtureRoot = Path.Combine(perfRoot, "fixtures");
        var workRoot = Path.Combine(perfRoot, "work");
        var reportRoot = Path.Combine(perfRoot, "reports");

        Directory.CreateDirectory(fixtureRoot);
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(reportRoot);

        Console.WriteLine($"Hodozip perf harness: mode={mode}, iterations={iterations}, warmup={warmup}");
        Console.WriteLine($"fixtures: {fixtureRoot}");
        Console.WriteLine();

        var fixtures = EnsureFixtures(fixtureRoot);
        if (!string.IsNullOrEmpty(only))
        {
            fixtures = fixtures.Where(x => x.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var results = new List<FixtureResult>();
        foreach (var fixture in fixtures)
        {
            Console.WriteLine($"=== {fixture.Name} ===");
            Console.WriteLine($"archive: {fixture.Path} ({FormatBytes(new FileInfo(fixture.Path).Length)})");

            var elapsed = new List<double>();
            var totalRuns = warmup + iterations;
            ExtractionResult? lastResult = null;
            var extractedBytes = 0L;

            for (var run = 0; run < totalRuns; run++)
            {
                var outputDirectory = Path.Combine(workRoot, $"{fixture.Name}-{mode}-{run}");
                DeleteDirectory(outputDirectory);
                Directory.CreateDirectory(outputDirectory);

                var options = new ExtractionOptions
                {
                    OverwritePolicy = OverwritePolicy.Overwrite,
                    FolderCollisionPolicy = FolderCollisionPolicy.Merge,
                    MaxDegreeOfParallelism = mode == "sequential" ? 1 : requestedParallelism,
                };

                var stopwatch = Stopwatch.StartNew();
                var result = await new SharpCompressExtractor().ExtractAsync(
                    new ExtractionRequest(fixture.Path, outputDirectory, options));
                stopwatch.Stop();

                if (!result.Success)
                {
                    Console.Error.WriteLine($"FAILED: {string.Join(" | ", result.Issues.Select(x => x.Message))}");
                    return 1;
                }

                lastResult = result;
                if (run >= warmup)
                {
                    elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
                    extractedBytes = MeasureDirectory(result.DestinationPath);
                    Console.WriteLine($"  run {run - warmup + 1}: {stopwatch.Elapsed.TotalMilliseconds:N0} ms, {FormatBytes(extractedBytes)}");
                }
                else
                {
                    Console.WriteLine($"  warmup: {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
                }

                DeleteDirectory(outputDirectory);
            }

            var median = Median(elapsed);
            var resultEntry = new FixtureResult
            {
                Name = fixture.Name,
                Description = fixture.Description,
                ArchiveBytes = new FileInfo(fixture.Path).Length,
                EntryCount = fixture.EntryCount,
                Mode = mode,
                MaxDegreeOfParallelism = mode == "sequential" ? 1 : requestedParallelism,
                Runs = iterations,
                ElapsedMilliseconds = elapsed,
                MedianMilliseconds = median,
                MegabytesPerSecond = median <= 0 ? 0 : extractedBytes / MiB / (median / 1000d),
                ExtractedBytes = extractedBytes,
                ExtractedFileCount = lastResult?.ExtractedFileCount ?? 0,
                SkippedFileCount = lastResult?.SkippedFileCount ?? 0,
                Success = true,
            };

            results.Add(resultEntry);
            Console.WriteLine($"  median: {median:N0} ms ({resultEntry.MegabytesPerSecond:N1} MiB/s)");
            Console.WriteLine();
        }

        var label = FindOption(args, "--label")
            ?? $"{mode}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var report = new PerfReport
        {
            Label = label,
            Timestamp = DateTimeOffset.Now,
            Mode = mode,
            RequestedParallelism = requestedParallelism,
            EffectiveProcessorCount = Environment.ProcessorCount,
            Machine = Environment.MachineName,
            Os = Environment.OSVersion.VersionString,
            Fixtures = results,
        };

        var reportPath = Path.Combine(reportRoot, label + ".json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"report: {reportPath}");
        return 0;
    }

    private static List<FixtureInfo> EnsureFixtures(string fixtureRoot)
    {
        var fixtures = new List<FixtureInfo>();

        var large = Path.Combine(fixtureRoot, "large-compressible.zip");
        if (!File.Exists(large))
        {
            Console.WriteLine("creating large-compressible.zip (16 x 64 MiB, highly compressible)...");
            using var stream = File.Create(large);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var pattern = CreatePattern((int)(1 * MiB));
            for (var entry = 0; entry < 16; entry++)
            {
                var zipEntry = archive.CreateEntry($"data/part{entry:00}.bin", CompressionLevel.SmallestSize);
                using var output = zipEntry.Open();
                for (var block = 0; block < 64; block++)
                {
                    output.Write(pattern);
                }
            }
        }

        fixtures.Add(new FixtureInfo("large-compressible", "16 x 64 MiB highly compressible entries", large, 16));

        var many = Path.Combine(fixtureRoot, "many-small-files.zip");
        if (!File.Exists(many))
        {
            Console.WriteLine("creating many-small-files.zip (10,000 entries)...");
            using var stream = File.Create(many);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            for (var index = 0; index < 10_000; index++)
            {
                var zipEntry = archive.CreateEntry(
                    $"files/group{index / 1000:00}/file{index:00000}.txt",
                    CompressionLevel.Optimal);
                using var output = new StreamWriter(zipEntry.Open(), Encoding.UTF8);
                output.Write($"file={index};");
                output.Write(new string('x', 512));
            }
        }

        fixtures.Add(new FixtureInfo("many-small-files", "10,000 small text files", many, 10_000));

        var stored = Path.Combine(fixtureRoot, "store-heavy.zip");
        if (!File.Exists(stored))
        {
            Console.WriteLine("creating store-heavy.zip (128 x 2 MiB stored)...");
            using var stream = File.Create(stored);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var random = new Random(98765);
            var data = new byte[2 * MiB];
            for (var entry = 0; entry < 128; entry++)
            {
                random.NextBytes(data);
                var zipEntry = archive.CreateEntry($"stored/blob{entry:000}.bin", CompressionLevel.NoCompression);
                using var output = zipEntry.Open();
                output.Write(data);
            }
        }

        fixtures.Add(new FixtureInfo("store-heavy", "128 x 2 MiB stored (incompressible) entries", stored, 128));

        var sevenZip = Path.Combine(fixtureRoot, "representative.7z");
        if (!File.Exists(sevenZip))
        {
            Console.WriteLine("creating representative.7z (7-Zip CLI)...");
            CreateSevenZipFixture(fixtureRoot, sevenZip);
        }

        fixtures.Add(new FixtureInfo("representative-7z", "80 mixed files compressed with 7-Zip", sevenZip, 80));
        return fixtures;
    }

    private static void CreateSevenZipFixture(string fixtureRoot, string sevenZipPath)
    {
        var sevenZip = FindSevenZip()
            ?? throw new InvalidOperationException("7z.exe が見つかりません。7-Zip をインストールするか PATH に追加してください。");

        var source = Path.Combine(fixtureRoot, "sevenzip-source");
        DeleteDirectory(source);
        Directory.CreateDirectory(source);

        var random = new Random(4242);
        var randomBlock = new byte[512 * 1024];
        random.NextBytes(randomBlock);
        var pattern = CreatePattern(512 * 1024);

        for (var index = 0; index < 80; index++)
        {
            var directory = Path.Combine(source, $"group{index / 20:00}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"file{index:000}.bin");
            using var output = File.Create(path);
            if (index % 2 == 0)
            {
                for (var block = 0; block < 4; block++)
                {
                    output.Write(randomBlock);
                }
            }
            else
            {
                for (var block = 0; block < 4; block++)
                {
                    output.Write(pattern);
                }
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZip,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("a");
        startInfo.ArgumentList.Add("-t7z");
        startInfo.ArgumentList.Add("-mx=5");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(sevenZipPath);
        startInfo.ArgumentList.Add(Path.Combine(source, "*"));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("7z.exe を起動できませんでした。");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"7z.exe が終了コード {process.ExitCode} で失敗しました。");
        }

        DeleteDirectory(source);
    }

    private static string? FindSevenZip()
    {
        var candidates = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            "7z.exe",
        };

        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                continue;
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(Path.PathSeparator))
            {
                try
                {
                    var full = Path.Combine(directory, candidate);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch
                {
                    // 不正な PATH 要素は無視する。
                }
            }
        }

        return null;
    }

    private static async Task<int> CompareAsync(string baselinePath, string currentPath, string? outputPath)
    {
        var baseline = JsonSerializer.Deserialize<PerfReport>(await File.ReadAllTextAsync(baselinePath), JsonOptions);
        var current = JsonSerializer.Deserialize<PerfReport>(await File.ReadAllTextAsync(currentPath), JsonOptions);
        if (baseline is null || current is null)
        {
            Console.Error.WriteLine("レポートを読み込めませんでした。");
            return 1;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"# Hodozip performance comparison");
        builder.AppendLine();
        builder.AppendLine($"- baseline: {baseline.Label} ({baseline.Timestamp:yyyy-MM-dd HH:mm:ss}, {baseline.Mode})");
        builder.AppendLine($"- current: {current.Label} ({current.Timestamp:yyyy-MM-dd HH:mm:ss}, {current.Mode})");
        builder.AppendLine($"- machine: {current.Machine}, {current.EffectiveProcessorCount} logical processors");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Archive | Entries | Baseline (ms) | Current (ms) | Speedup | Baseline MiB/s | Current MiB/s |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var fixture in baseline.Fixtures)
        {
            var match = current.Fixtures.FirstOrDefault(x => x.Name == fixture.Name);
            if (match is null)
            {
                continue;
            }

            var speedup = match.MedianMilliseconds <= 0 ? 0 : fixture.MedianMilliseconds / match.MedianMilliseconds;
            builder.AppendLine(
                $"| {fixture.Name} | {FormatBytes(fixture.ArchiveBytes)} | {fixture.EntryCount:N0} | " +
                $"{fixture.MedianMilliseconds:N0} | {match.MedianMilliseconds:N0} | {speedup:N2}x | " +
                $"{fixture.MegabytesPerSecond:N1} | {match.MegabytesPerSecond:N1} |");
        }

        builder.AppendLine();
        builder.AppendLine("Notes:");
        builder.AppendLine();
        builder.AppendLine("- Parallel extraction is enabled for non-encrypted ZIP only. 7z, RAR, TAR, MSI, and encrypted ZIP use the sequential path.");
        builder.AppendLine("- representative-7z therefore measures the sequential path in both runs; the small difference is run-to-run noise.");
        builder.AppendLine("- The large-compressible fixture expands to 1 GiB (16 x 64 MiB entries).");

        var markdown = builder.ToString();
        Console.WriteLine(markdown);

        var path = outputPath
            ?? Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(currentPath)) ?? ".",
                $"comparison-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, markdown);
        Console.WriteLine($"comparison: {path}");
        return 0;
    }

    private static byte[] CreatePattern(int length)
    {
        var data = new byte[length];
        for (var index = 0; index < length; index++)
        {
            data[index] = (byte)('A' + index % 26);
        }

        return data;
    }

    private static long MeasureDirectory(string directory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
                // 計測中のファイル消失は無視する。
            }
        }

        return total;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 作業ディレクトリの削除失敗は無視する。
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hodozip.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string? FindOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(x => x).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
    }

    private sealed record FixtureInfo(string Name, string Description, string Path, int EntryCount);

    private sealed class PerfReport
    {
        public string Label { get; set; } = string.Empty;

        public DateTimeOffset Timestamp { get; set; }

        public string Mode { get; set; } = string.Empty;

        public int RequestedParallelism { get; set; }

        public int EffectiveProcessorCount { get; set; }

        public string Machine { get; set; } = string.Empty;

        public string Os { get; set; } = string.Empty;

        public List<FixtureResult> Fixtures { get; set; } = new();
    }

    private sealed class FixtureResult
    {
        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public long ArchiveBytes { get; set; }

        public int EntryCount { get; set; }

        public string Mode { get; set; } = string.Empty;

        public int MaxDegreeOfParallelism { get; set; }

        public int Runs { get; set; }

        public List<double> ElapsedMilliseconds { get; set; } = new();

        public double MedianMilliseconds { get; set; }

        public double MegabytesPerSecond { get; set; }

        public long ExtractedBytes { get; set; }

        public int ExtractedFileCount { get; set; }

        public int SkippedFileCount { get; set; }

        public bool Success { get; set; }
    }
}
