using System.Diagnostics;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SharpCompressCompressionType = SharpCompress.Common.CompressionType;

namespace Hodozip.Core;

/// <summary>
/// SharpCompress（ZIP / 7z）と SharpZipLib（WinZip AES ZIP）を使った圧縮エンジン。
/// 7z のパスワード暗号化は SharpCompress の writer が対応していないため、
/// パスワードは ZIP にだけ適用する。
/// </summary>
public sealed class SharpCompressCompressor : IArchiveCompressor
{
    private const int CopyBufferSize = 81920;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(120);

    public Task<CompressionResult> CompressAsync(
        CompressionRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CompressCore(request, cancellationToken));
    }

    private static CompressionResult CompressCore(
        CompressionRequest request,
        CancellationToken cancellationToken)
    {
        EncodingSetup.EnsureRegistered();

        if (cancellationToken.IsCancellationRequested)
        {
            return new CompressionResult
            {
                Success = false,
                Cancelled = true,
                OutputPath = request.OutputPath,
            };
        }

        var outputPath = CompressionDefaults.TrimPath(request.OutputPath);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return Failure(outputPath, "圧縮先が指定されていません。", "Compression_ErrorOutputMissing");
        }

        var requestedOutput = request.Options.Overwrite
            ? outputPath
            : CompressionDefaults.GetUniqueOutputPath(outputPath);

        var format = request.Options.Format;
        var password = string.IsNullOrEmpty(request.Options.Password)
            ? null
            : request.Options.Password;
        var passwordIgnored = format == ArchiveCreationFormat.SevenZip && password is not null;

        IReadOnlyList<CompressionEntry> entries;
        try
        {
            entries = CompressionPlanner.Plan(request.SourcePaths, requestedOutput);
        }
        catch (FileNotFoundException ex)
        {
            return Failure(requestedOutput, ex.Message, "Compression_ErrorSourceMissing", ex.FileName ?? ex.Message);
        }
        catch (Exception ex)
        {
            return Failure(requestedOutput, ex.Message, "Compression_ErrorUnexpected", ex.Message);
        }

        var totalBytes = entries.Where(x => !x.IsDirectory).Sum(x => x.Length);
        var fileCount = entries.Count(x => !x.IsDirectory);
        var directoryCount = entries.Count(x => x.IsDirectory);

        try
        {
            var outputDirectory = Path.GetDirectoryName(requestedOutput);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Failure(requestedOutput, "圧縮先フォルダーを特定できません。", "Compression_ErrorOutputParent");
            }

            Directory.CreateDirectory(outputDirectory);

            var temporaryPath = requestedOutput + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.SequentialScan))
                {
                    if (format == ArchiveCreationFormat.SevenZip)
                    {
                        WriteSevenZip(output, entries, request.Options, totalBytes, requestedOutput, cancellationToken);
                    }
                    else
                    {
                        WriteZip(output, entries, request.Options, password, totalBytes, requestedOutput, cancellationToken);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                File.Move(temporaryPath, requestedOutput, overwrite: true);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }

            var outputBytes = new FileInfo(requestedOutput).Length;
            return new CompressionResult
            {
                Success = true,
                OutputPath = requestedOutput,
                FileCount = fileCount,
                DirectoryCount = directoryCount,
                SourceBytes = totalBytes,
                OutputBytes = outputBytes,
                PasswordIgnored = passwordIgnored,
            };
        }
        catch (OperationCanceledException)
        {
            return new CompressionResult
            {
                Success = false,
                Cancelled = true,
                OutputPath = requestedOutput,
                FileCount = fileCount,
                DirectoryCount = directoryCount,
                SourceBytes = totalBytes,
                PasswordIgnored = passwordIgnored,
            };
        }
        catch (Exception ex)
        {
            return Failure(
                requestedOutput,
                ex.Message,
                "Compression_ErrorUnexpected",
                new object?[] { ex.Message },
                fileCount,
                directoryCount,
                totalBytes,
                passwordIgnored);
        }
    }

    /// <summary>
    /// ZIP を書き出す。平文 ZIP と AES ZIP の両方を SharpZipLib で扱う。
    ///
    /// SharpCompress 0.50.8 の ZipWriter はディレクトリ エントリのローカル
    /// ヘッダーに Deflate を、セントラル ディレクトリに Stored を書き込むため、
    /// 7-Zip や Explorer が "Headers Error" で開けなくなる。SharpZipLib は
    /// 両方を一貫して書き、AES 暗号化にも対応しているため、ZIP はこちらで
    /// 生成する（7z は SharpCompress の SevenZipWriter を使う）。
    /// </summary>
    private static void WriteZip(
        Stream output,
        IReadOnlyList<CompressionEntry> entries,
        CompressionOptions options,
        string? password,
        long totalBytes,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var isUtf8 = options.ZipNameEncoding == ZipNameEncoding.Utf8;
        var codec = StringCodec.FromEncoding(isUtf8
            ? Encoding.UTF8
            : Encoding.GetEncoding(932));
        if (!isUtf8)
        {
            // SharpZipLib は既定で UTF-8 名を書き込むため、Shift_JIS 指定時は
            // レガシー エンコーディングを強制する。
            codec = codec.WithForcedLegacyEncoding();
        }

        // AES の場合、Size / Crc を事前に設定しないとデータディスクリプターが
        // 書かれ、SharpCompress が WinZip AES の認証に失敗する。
        using var zip = new ZipOutputStream(output, codec)
        {
            IsStreamOwner = false,
            UseZip64 = UseZip64.Dynamic,
        };
        zip.SetLevel(options.Level switch
        {
            ArchiveCompressionLevel.Maximum => 9,
            ArchiveCompressionLevel.Store => 0,
            _ => 6,
        });

        long processedBeforeCurrent = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                // ディレクトリ エントリまで暗号化すると SharpCompress が読めないため、
                // パスワードを外して格納する。
                zip.Password = null!;
                var directoryEntry = new ZipEntry(entry.EntryPath.TrimEnd('/') + "/")
                {
                    DateTime = entry.ModifiedTime,
                    CompressionMethod = CompressionMethod.Stored,
                    Size = 0,
                    Crc = 0,
                    IsCrypted = false,
                    AESKeySize = 0,
                    IsUnicodeText = isUtf8,
                };
                zip.PutNextEntry(directoryEntry);
                zip.CloseEntry();
                continue;
            }

            options.Progress?.Report(new CompressionProgress(
                processedBeforeCurrent,
                totalBytes,
                entry.EntryPath,
                0,
                entry.Length,
                outputPath));

            zip.Password = password ?? null!;
            var zipEntry = new ZipEntry(entry.EntryPath)
            {
                DateTime = entry.ModifiedTime,
                CompressionMethod = options.Level == ArchiveCompressionLevel.Store
                    ? CompressionMethod.Stored
                    : CompressionMethod.Deflated,
                IsUnicodeText = isUtf8,
            };

            if (password is not null)
            {
                zipEntry.Size = entry.Length;
                zipEntry.Crc = 0;
                zipEntry.AESKeySize = 256;
            }

            zip.PutNextEntry(zipEntry);

            using (var source = new FileStream(
                entry.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.SequentialScan))
            {
                CopyWithProgress(
                    source,
                    zip,
                    entry.Length,
                    processedBeforeCurrent,
                    totalBytes,
                    entry.EntryPath,
                    outputPath,
                    options.Progress,
                    cancellationToken);
            }

            zip.CloseEntry();

            processedBeforeCurrent += entry.Length;
            options.Progress?.Report(new CompressionProgress(
                processedBeforeCurrent,
                totalBytes,
                entry.EntryPath,
                entry.Length,
                entry.Length,
                outputPath));
        }
    }

    private static void WriteSevenZip(
        Stream output,
        IReadOnlyList<CompressionEntry> entries,
        CompressionOptions options,
        long totalBytes,
        string outputPath,
        CancellationToken cancellationToken)
    {
        // SharpCompress の 7z writer は LZMA / LZMA2 だけに対応する。
        // Store は LZMA2 の最低レベルとして扱う。
        var sevenOptions = new SevenZipWriterOptions(SharpCompressCompressionType.LZMA2)
        {
            CompressionLevel = options.Level switch
            {
                ArchiveCompressionLevel.Store => 0,
                ArchiveCompressionLevel.Maximum => 9,
                _ => 5,
            },
        };

        using var writer = new SevenZipWriter(output, sevenOptions);
        WriteEntriesAsync(writer, entries, totalBytes, outputPath, options, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task WriteEntriesAsync(
        AbstractWriter writer,
        IReadOnlyList<CompressionEntry> entries,
        long totalBytes,
        string outputPath,
        CompressionOptions options,
        CancellationToken cancellationToken)
    {
        long processedBeforeCurrent = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                await writer
                    .WriteDirectoryAsync(entry.EntryPath, entry.ModifiedTime, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            options.Progress?.Report(new CompressionProgress(
                processedBeforeCurrent,
                totalBytes,
                entry.EntryPath,
                0,
                entry.Length,
                outputPath));

            using var source = new FileStream(
                entry.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.SequentialScan);
            using var progressStream = new ProgressReadStream(
                source,
                entry.Length,
                processedBeforeCurrent,
                totalBytes,
                entry.EntryPath,
                outputPath,
                options.Progress);

            await writer
                .WriteAsync(entry.EntryPath, progressStream, entry.ModifiedTime, cancellationToken)
                .ConfigureAwait(false);

            processedBeforeCurrent += entry.Length;
            options.Progress?.Report(new CompressionProgress(
                processedBeforeCurrent,
                totalBytes,
                entry.EntryPath,
                entry.Length,
                entry.Length,
                outputPath));
        }
    }

    private static void CopyWithProgress(
        Stream source,
        Stream destination,
        long fileLength,
        long processedBeforeCurrent,
        long totalBytes,
        string entryPath,
        string outputPath,
        IProgress<CompressionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        long fileProcessed = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        while (fileProcessed < fileLength)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var toRead = (int)Math.Min(buffer.Length, fileLength - fileProcessed);
            var read = source.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                throw new EndOfStreamException("圧縮中にファイルの終端へ到達しました。");
            }

            destination.Write(buffer, 0, read);
            fileProcessed += read;

            if (stopwatch.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = stopwatch.Elapsed;
                progress?.Report(new CompressionProgress(
                    processedBeforeCurrent + fileProcessed,
                    totalBytes,
                    entryPath,
                    fileProcessed,
                    fileLength,
                    outputPath));
            }
        }
    }

    private static CompressionResult Failure(
        string outputPath,
        string message,
        string messageKey,
        params object?[] arguments)
    {
        return Failure(outputPath, message, messageKey, arguments, 0, 0, 0, false);
    }

    private static CompressionResult Failure(
        string outputPath,
        string message,
        string messageKey,
        object?[] arguments,
        int fileCount,
        int directoryCount,
        long sourceBytes,
        bool passwordIgnored)
    {
        return new CompressionResult
        {
            Success = false,
            OutputPath = outputPath,
            FileCount = fileCount,
            DirectoryCount = directoryCount,
            SourceBytes = sourceBytes,
            PasswordIgnored = passwordIgnored,
            ErrorMessage = message,
            ErrorMessageKey = messageKey,
            ErrorArguments = arguments,
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 一時ファイルの削除失敗は、元のエラーを隠さない。
        }
    }

    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _fileLength;
        private readonly long _processedBeforeCurrent;
        private readonly long _totalBytes;
        private readonly string _entryPath;
        private readonly string _outputPath;
        private readonly IProgress<CompressionProgress>? _progress;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _fileProcessed;
        private TimeSpan _lastReport;

        public ProgressReadStream(
            Stream inner,
            long fileLength,
            long processedBeforeCurrent,
            long totalBytes,
            string entryPath,
            string outputPath,
            IProgress<CompressionProgress>? progress)
        {
            _inner = inner;
            _fileLength = fileLength;
            _processedBeforeCurrent = processedBeforeCurrent;
            _totalBytes = totalBytes;
            _entryPath = entryPath;
            _outputPath = outputPath;
            _progress = progress;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _fileLength;

        public override long Position
        {
            get => _fileProcessed;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Advance(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Advance(read);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        private void Advance(int read)
        {
            if (read <= 0)
            {
                return;
            }

            _fileProcessed += read;
            if (_stopwatch.Elapsed - _lastReport < ProgressInterval)
            {
                return;
            }

            _lastReport = _stopwatch.Elapsed;
            _progress?.Report(new CompressionProgress(
                _processedBeforeCurrent + _fileProcessed,
                _totalBytes,
                _entryPath,
                _fileProcessed,
                _fileLength,
                _outputPath));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
