namespace Hodozip.Core;

/// <summary>
/// 更新用 setup.exe のダウンロードと SHA256 digest の照合。
/// HTTP クライアントを注入できるようにし、ネットワークに依存せずテストできる。
/// </summary>
public static class UpdateDownloader
{
    private const int BufferSize = 81920;

    /// <summary>
    /// setup.exe をダウンロードし、GitHub API の digest と一致した場合だけ
    /// ローカル パスを返す。失敗または不一致の場合は null を返す。
    /// </summary>
    public static async Task<string?> DownloadSetupAsync(
        UpdateRelease release,
        HttpClient httpClient,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        string? downloadDirectory = null)
    {
        var asset = release.SetupAsset;
        if (asset is null || string.IsNullOrWhiteSpace(asset.Sha256))
        {
            return null;
        }

        var targetDirectory = downloadDirectory
            ?? Path.Combine(Path.GetTempPath(), "HodozipUpdate");
        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, asset.Name);

        try
        {
            if (File.Exists(targetPath)
                && await Sha256Verifier
                    .VerifyFileAsync(targetPath, asset.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                progress?.Report(1);
                return targetPath;
            }

            var temporaryPath = targetPath + ".download";
            try
            {
                using var response = await httpClient
                    .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? asset.Size;
                await using (var source = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false))
                await using (var destination = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan))
                {
                    var buffer = new byte[BufferSize];
                    long downloaded = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        downloaded += read;
                        if (total > 0)
                        {
                            progress?.Report(Math.Clamp((double)downloaded / total, 0, 1));
                        }
                    }
                }

                if (!await Sha256Verifier
                        .VerifyFileAsync(temporaryPath, asset.Sha256, cancellationToken)
                        .ConfigureAwait(false))
                {
                    File.Delete(temporaryPath);
                    return null;
                }

                File.Move(temporaryPath, targetPath, overwrite: true);
                progress?.Report(1);
                return targetPath;
            }
            catch
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // 一時ファイルの削除失敗は元のエラーを隠さない。
                }

                return null;
            }
        }
        catch
        {
            return null;
        }
    }
}
