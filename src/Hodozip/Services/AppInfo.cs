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

        Copyright (c) 2026 Ren9618

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
