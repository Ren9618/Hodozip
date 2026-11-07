using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hodozip.Core.Tests;

public sealed class UpdateCheckTests : IDisposable
{
    private readonly string _root;

    public UpdateCheckTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "HodozipUpdateTests", Guid.NewGuid().ToString("N"));
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
    [InlineData("0.5.0", 0, 5, 0)]
    [InlineData("v0.5.0", 0, 5, 0)]
    [InlineData("V1.2", 1, 2, 0)]
    [InlineData("1.2.3.4", 1, 2, 3)]
    [InlineData("v2.0.0-beta.1", 2, 0, 0)]
    [InlineData("0.10.2+build", 0, 10, 2)]
    public void TryParse_AcceptsVersionTags(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("v")]
    [InlineData("-1.0.0")]
    [InlineData("1..2")]
    [InlineData("1.2.3.4.5")]
    public void TryParse_RejectsInvalidVersions(string text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("0.5.0", "0.4.0", true)]
    [InlineData("0.4.1", "0.4.0", true)]
    [InlineData("0.4.0", "0.4.0", false)]
    [InlineData("0.3.9", "0.4.0", false)]
    [InlineData("0.10.0", "0.9.9", true)]
    public void IsNewerThan_ComparesNumerically(string candidate, string current, bool expected)
    {
        Assert.True(ReleaseVersion.TryParse(candidate, out var candidateVersion));
        Assert.True(ReleaseVersion.TryParse(current, out var currentVersion));
        Assert.Equal(expected, candidateVersion.IsNewerThan(currentVersion));
    }

    [Fact]
    public void ParseRelease_ReadsAssetsAndDigest()
    {
        using var document = JsonDocument.Parse(ReleaseJson);
        var release = UpdateCheckClient.ParseRelease(document.RootElement);

        Assert.NotNull(release);
        Assert.Equal("v0.5.0", release.TagName);
        Assert.Equal(new ReleaseVersion(0, 5, 0), release.Version);
        Assert.Equal("https://github.com/Ren9618/Hodozip/releases/tag/v0.5.0", release.ReleasePageUrl);
        Assert.NotNull(release.SetupAsset);
        Assert.Equal("Hodozip-0.5.0-setup.exe", release.SetupAsset.Name);
        Assert.Equal(new string('A', 64), release.SetupAsset.Sha256);
        Assert.NotNull(release.ZipAsset);
        Assert.Equal("Hodozip-0.5.0-win-x64.zip", release.ZipAsset.Name);
    }

    [Theory]
    [InlineData(true, null, 0, true)]
    [InlineData(true, 23, 0, false)]
    [InlineData(true, 24, 0, true)]
    [InlineData(true, 25, 0, true)]
    [InlineData(false, null, 0, false)]
    [InlineData(false, 25, 0, false)]
    [InlineData(true, -1, 0, false)]
    public void ShouldCheck_RespectsToggleAndDailyThrottle(
        bool enabled,
        int? hoursSinceLastCheck,
        int minutesSinceLastCheck,
        bool expected)
    {
        var now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        var lastCheck = hoursSinceLastCheck is null
            ? (DateTimeOffset?)null
            : now
                .AddHours(-hoursSinceLastCheck.Value)
                .AddMinutes(-minutesSinceLastCheck);

        Assert.Equal(
            expected,
            UpdatePolicy.ShouldCheck(enabled, lastCheck, now));
    }

    [Theory]
    [InlineData("0.5.0", "0.5.0", true)]
    [InlineData("0.5.0", "0.6.0", true)]
    [InlineData("0.5.0", "0.4.9", false)]
    [InlineData("0.5.0", "v0.5.0", true)]
    [InlineData("0.5.0", "invalid", false)]
    [InlineData("0.5.0", null, false)]
    [InlineData("0.5.0", "  ", false)]
    public void IsVersionSkipped_ComparesStoredVersion(string release, string? skipped, bool expected)
    {
        Assert.True(ReleaseVersion.TryParse(release, out var version));
        Assert.Equal(expected, UpdatePolicy.IsVersionSkipped(version, skipped));
    }

    [Fact]
    public async Task DownloadSetup_ReturnsPathWhenDigestMatches()
    {
        var payload = Encoding.UTF8.GetBytes("Hodozip setup payload");
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        var release = CreateRelease(digest);
        using var client = CreateBytesClient(payload, HttpStatusCode.OK);
        var downloadDirectory = Path.Combine(_root, "downloads");

        var path = await UpdateDownloader.DownloadSetupAsync(
            release,
            client,
            downloadDirectory: downloadDirectory);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path!));
    }

    [Fact]
    public async Task DownloadSetup_RejectsDigestMismatchAndDeletesFile()
    {
        var payload = Encoding.UTF8.GetBytes("Hodozip setup payload");
        var release = CreateRelease(new string('0', 64));
        using var client = CreateBytesClient(payload, HttpStatusCode.OK);
        var downloadDirectory = Path.Combine(_root, "downloads");

        var path = await UpdateDownloader.DownloadSetupAsync(
            release,
            client,
            downloadDirectory: downloadDirectory);

        Assert.Null(path);
        Assert.False(File.Exists(Path.Combine(downloadDirectory, "Hodozip-0.5.0-setup.exe")));
        Assert.Empty(Directory.GetFiles(downloadDirectory));
    }

    [Fact]
    public async Task DownloadSetup_ReturnsNullWithoutDigest()
    {
        var release = CreateRelease(null);
        using var client = CreateBytesClient(Array.Empty<byte>(), HttpStatusCode.OK);

        var path = await UpdateDownloader.DownloadSetupAsync(
            release,
            client,
            downloadDirectory: Path.Combine(_root, "downloads"));

        Assert.Null(path);
    }

    [Fact]
    public async Task CheckForUpdate_ReturnsNewerRelease()
    {
        using var client = CreateClient(ReleaseJson);
        var release = await new UpdateCheckClient(client).CheckForUpdateAsync("0.4.0");

        Assert.NotNull(release);
        Assert.Equal("0.5.0", release.Version.ToString());
    }

    [Fact]
    public async Task CheckForUpdate_ReturnsNullWhenCurrentIsLatest()
    {
        using var client = CreateClient(ReleaseJson);
        var release = await new UpdateCheckClient(client).CheckForUpdateAsync("0.5.0");

        Assert.Null(release);
    }

    [Fact]
    public async Task CheckForUpdate_ThrowsOnHttpFailure()
    {
        using var client = CreateClient("{}", HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(
            () => new UpdateCheckClient(client).CheckForUpdateAsync("0.4.0"));
    }

    [Fact]
    public async Task Sha256Verifier_AcceptsMatchingDigest()
    {
        var path = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("Hodozip update payload"));
        var expected = Sha256Verifier.ComputeHex(path);

        Assert.True(await Sha256Verifier.VerifyFileAsync(path, expected));
        Assert.True(await Sha256Verifier.VerifyFileAsync(path, expected.ToLowerInvariant()));
    }

    [Fact]
    public async Task Sha256Verifier_RejectsMismatchAndMalformedValues()
    {
        var path = Path.Combine(_root, "payload.bin");
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("Hodozip update payload"));

        Assert.False(await Sha256Verifier.VerifyFileAsync(path, new string('0', 64)));
        Assert.False(await Sha256Verifier.VerifyFileAsync(path, "not-a-digest"));
        Assert.False(await Sha256Verifier.VerifyFileAsync(path, string.Empty));
    }

    private static UpdateRelease CreateRelease(string? digest)
    {
        return new UpdateRelease(
            "v0.5.0",
            new ReleaseVersion(0, 5, 0),
            "https://example.test/releases/v0.5.0",
            "Hodozip v0.5.0",
            new UpdateAsset(
                "Hodozip-0.5.0-setup.exe",
                "https://example.test/Hodozip-0.5.0-setup.exe",
                22,
                digest),
            null);
    }

    private static HttpClient CreateClient(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpClient(new StubHandler(body, status))
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private static HttpClient CreateBytesClient(byte[] body, HttpStatusCode status)
    {
        return new HttpClient(new BytesHandler(body, status))
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }

    private sealed class BytesHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly HttpStatusCode _status;

        public BytesHandler(byte[] body, HttpStatusCode status)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(_body),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }

    private const string ReleaseJson = """
        {
          "tag_name": "v0.5.0",
          "name": "Hodozip v0.5.0",
          "html_url": "https://github.com/Ren9618/Hodozip/releases/tag/v0.5.0",
          "assets": [
            {
              "name": "Hodozip-0.5.0-setup.exe",
              "browser_download_url": "https://example.test/Hodozip-0.5.0-setup.exe",
              "size": 61000000,
              "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            },
            {
              "name": "Hodozip-0.5.0-win-x64.zip",
              "browser_download_url": "https://example.test/Hodozip-0.5.0-win-x64.zip",
              "size": 89000000,
              "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            },
            {
              "name": "Hodozip-0.5.0.msix",
              "browser_download_url": "https://example.test/Hodozip-0.5.0.msix",
              "size": 88000000,
              "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
            }
          ]
        }
        """;
}
