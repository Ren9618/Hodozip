using System.Diagnostics;
using System.Net.Http;
using Hodozip.Core;

namespace Hodozip.Services;

/// <summary>
/// 起動時の更新確認と、セットアップ版のダウンロード・検証・起動を行う。
/// MSIX 版では自動更新を利用できないため、すべて無効になる。
/// </summary>
internal sealed class UpdateService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    /// <summary>
    /// 設定に従って最新リリースを確認する。1 日 1 回を超える確認は行わない。
    /// ネットワークや API の失敗はすべて握りつぶして null を返す。
    /// </summary>
    public async Task<UpdateRelease?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!AppInfo.SupportsAutoUpdate)
        {
            return null;
        }

        var settings = SettingsService.Load();
        var now = DateTimeOffset.UtcNow;
        if (!UpdatePolicy.ShouldCheck(
                settings.CheckForUpdatesOnStartup,
                settings.LastUpdateCheckUtc,
                now))
        {
            return null;
        }

        // 失敗しても同じ日に何度も問い合わせないよう、試行時刻を先に保存する。
        settings.LastUpdateCheckUtc = now;
        SettingsService.Save(settings);

        try
        {
            var client = new UpdateCheckClient(
                SharedHttpClient,
                new Uri(AppInfo.LatestReleaseApiUrl));
            var release = await client
                .CheckForUpdateAsync(AppInfo.Version, cancellationToken)
                .ConfigureAwait(false);

            if (release is null)
            {
                return null;
            }

            if (UpdatePolicy.IsVersionSkipped(release.Version, settings.SkippedUpdateVersion))
            {
                return null;
            }

            return release;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>このバージョンをスキップとして保存する。</summary>
    public static void SkipVersion(UpdateRelease release)
    {
        var settings = SettingsService.Load();
        settings.SkippedUpdateVersion = release.Version.ToString();
        SettingsService.Save(settings);
    }

    /// <summary>
    /// セットアップ版をダウンロードし、GitHub の SHA256 digest と照合する。
    /// 検証に失敗した場合は null を返す。
    /// </summary>
    public Task<string?> DownloadSetupAsync(
        UpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return UpdateDownloader.DownloadSetupAsync(
            release,
            SharedHttpClient,
            progress,
            cancellationToken);
    }

    /// <summary>Inno Setup のサイレント インストールを開始する。</summary>
    public static bool TryLaunchInstaller(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ポータブル zip 版向けのフォールバック。リリースページを開く。</summary>
    public static bool OpenReleasePage(UpdateRelease release)
    {
        var url = string.IsNullOrWhiteSpace(release.ReleasePageUrl)
            ? AppInfo.ReleasesUrl
            : release.ReleasePageUrl;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
