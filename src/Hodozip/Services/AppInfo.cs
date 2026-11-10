using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Hodozip.Core;

namespace Hodozip.Services;

internal static class AppInfo
{
    private const int AppModelErrorNoPackage = 15700;

    public const string ProductName = "Hodozip";

    public const string GitHubSponsorsUrl = "https://github.com/sponsors/Ren9618";

    public const string StripeDonationUrl = "https://buy.stripe.com/5kQ9AU8Xva8T7tAbnj67S00";

    /// <summary>フィードバック フォーム。UI から参照する URL はここ 1 か所にまとめる。</summary>
    public const string FeedbackUrl =
        "https://docs.google.com/forms/d/e/1FAIpQLScFpnTH8fFGBcCQfoNoN7O-mGnPb1u8aLhShMMQZ8uFLxQ9GQ/viewform";

    public const string ReleasesUrl = "https://github.com/Ren9618/Hodozip/releases";

    public const string LatestReleaseApiUrl = "https://api.github.com/repos/Ren9618/Hodozip/releases/latest";

    private const string UninstallRegistryKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7C4E2B1-6D3F-4E8A-9B5C-1F2D3E4A5B6C}_is1";

    /// <summary>Inno Setup 版としてインストールされているか。</summary>
    public static bool IsSetupInstalled
    {
        get
        {
            if (IsPackaged)
            {
                return false;
            }

            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallRegistryKey);
                var location = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(location))
                {
                    return string.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // レジストリを読めない場合はパスで判定する。
            }

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Hodozip");
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>自動アップデートは unpackaged 版だけが対象。</summary>
    public static bool SupportsAutoUpdate => !IsPackaged;

    public static string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null
                ? "0.0.0"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    /// <summary>MSIX パッケージとして実行されているか。</summary>
    public static bool IsPackaged
    {
        get
        {
            try
            {
                var length = 0;
                return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>README やバージョン情報に表示する対応形式の一覧。</summary>
    public static string SupportedFormatsText =>
        string.Join(" / ", ArchiveFormats.SupportedExtensions.Select(x => x.TrimStart('.').ToUpperInvariant()));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    public const string MitLicenseText =
        """
        MIT License

        Copyright (c) 2026 Rayno Works (Ren9618)

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """;
}
