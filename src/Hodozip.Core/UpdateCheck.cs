using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hodozip.Core;

/// <summary>GitHub Releases のバージョン表記を比較する。</summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch)
    : IComparable<ReleaseVersion>
{
    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // プレリリース / ビルドメタデータは比較対象外にする。
        var separator = text.IndexOfAny(new[] { '-', '+' });
        if (separator >= 0)
        {
            text = text[..separator];
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        Span<int> numbers = stackalloc int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < 0)
            {
                return false;
            }

            numbers[index] = number;
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        return result != 0 ? result : Patch.CompareTo(other.Patch);
    }

    public bool IsNewerThan(ReleaseVersion other) => CompareTo(other) > 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>GitHub Release のアセット。</summary>
public sealed record UpdateAsset(
    string Name,
    string DownloadUrl,
    long Size,
    string? Sha256);

/// <summary>GitHub の最新リリース情報。</summary>
public sealed record UpdateRelease(
    string TagName,
    ReleaseVersion Version,
    string ReleasePageUrl,
    string? ReleaseName,
    UpdateAsset? SetupAsset,
    UpdateAsset? ZipAsset);

/// <summary>
/// GitHub Releases API から最新リリースを取得する。ネットワーク失敗は呼び出し側で
/// 無視できるよう、例外をそのまま伝播させる（アプリ側で握りつぶす）。
/// </summary>
public sealed class UpdateCheckClient
{
    public static readonly Uri DefaultLatestReleaseUri =
        new("https://api.github.com/repos/Ren9618/Hodozip/releases/latest");

    private readonly HttpClient _httpClient;
    private readonly Uri _latestReleaseUri;

    public UpdateCheckClient(HttpClient httpClient, Uri? latestReleaseUri = null)
    {
        _httpClient = httpClient;
        _latestReleaseUri = latestReleaseUri ?? DefaultLatestReleaseUri;
    }

    /// <summary>現在のバージョンより新しいリリースだけを返す。</summary>
    public async Task<UpdateRelease?> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
        {
            return null;
        }

        var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
        return release is not null && release.Version.IsNewerThan(current) ? release : null;
    }

    public async Task<UpdateRelease?> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Hodozip-UpdateChecker");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return ParseRelease(document.RootElement);
    }

    public static UpdateRelease? ParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tagName = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tagName)
            || !ReleaseVersion.TryParse(tagName, out var version))
        {
            return null;
        }

        var releasePageUrl = GetString(root, "html_url") ?? string.Empty;
        var releaseName = GetString(root, "name");

        UpdateAsset? setupAsset = null;
        UpdateAsset? zipAsset = null;
        var expectedSetupName = $"Hodozip-{version}-setup.exe";

        if (root.TryGetProperty("assets", out var assets)
            && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var name = GetString(asset, "name");
                var downloadUrl = GetString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                var digest = ParseSha256(GetString(asset, "digest"));
                var size = asset.TryGetProperty("size", out var sizeElement)
                    && sizeElement.TryGetInt64(out var parsedSize)
                        ? parsedSize
                        : 0;

                var updateAsset = new UpdateAsset(name, downloadUrl, size, digest);

                if (string.Equals(name, expectedSetupName, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    setupAsset ??= updateAsset;
                }
                else if (name.StartsWith("Hodozip-", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipAsset ??= updateAsset;
                }
            }
        }

        return new UpdateRelease(
            tagName,
            version,
            releasePageUrl,
            releaseName,
            setupAsset,
            zipAsset);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string? ParseSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        const string prefix = "sha256:";
        var value = digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : digest;

        value = value.Trim();
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            return null;
        }

        return value.ToUpperInvariant();
    }
}

/// <summary>ダウンロードしたファイルの SHA256 を検証する。</summary>
public static class Sha256Verifier
{
    public static async Task<bool> VerifyFileAsync(
        string path,
        string expectedHex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
        {
            return false;
        }

        var expected = expectedHex.Trim();
        if (expected.Length != 64)
        {
            return false;
        }

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expected);
        }
        catch (FormatException)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actual, expectedBytes);
    }

    public static string ComputeHex(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
