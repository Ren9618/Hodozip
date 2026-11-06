using System.Text;

namespace Hodozip.Core;

internal sealed record SanitizedPath(string RelativePath, bool Changed, string? Note);

internal static class PathSanitizer
{
    private static readonly char[] InvalidNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// ZIP 内のパスを Windows の相対パスへ正規化する。
    /// 絶対パスや .. を含むパスはセキュリティ上拒否する。
    /// </summary>
    public static bool TryNormalize(string? entryPath, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return false;
        }

        var value = entryPath.Trim();
        if (value.StartsWith('/') || value.StartsWith('\\'))
        {
            error = "絶対パスを指すエントリを拒否しました。";
            return false;
        }

        if (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
        {
            error = "ドライブレターを含むエントリを拒否しました。";
            return false;
        }

        var rawSegments = value.Replace('/', '\\').Split('\\');
        var segments = new List<string>(rawSegments.Length);

        foreach (var rawSegment in rawSegments)
        {
            if (rawSegment.Length == 0 || rawSegment == ".")
            {
                continue;
            }

            if (rawSegment == "..")
            {
                error = "解凍先の外を参照する .. を含むエントリを拒否しました。";
                return false;
            }

            if (rawSegment.Contains('\0'))
            {
                error = "ヌル文字を含むエントリを拒否しました。";
                return false;
            }

            segments.Add(rawSegment);
        }

        if (segments.Count == 0)
        {
            return false;
        }

        normalized = string.Join('\\', segments);
        return true;
    }

    /// <summary>
    /// Windows で作成できない名前を安全な名前に変換する。
    /// 変換前後が同じ場合は Changed = false。
    /// </summary>
    public static SanitizedPath Sanitize(string relativePath)
    {
        var segments = relativePath.Split('\\');
        var changed = false;
        var notes = new List<string>();

        for (var i = 0; i < segments.Length; i++)
        {
            var original = segments[i];
            var sanitized = SanitizeSegment(original, out var segmentChanged, out var reason);
            if (segmentChanged)
            {
                changed = true;
                notes.Add($"{original} -> {sanitized}" + (reason is null ? string.Empty : $" ({reason})"));
            }

            segments[i] = sanitized;
        }

        return new SanitizedPath(
            string.Join('\\', segments),
            changed,
            notes.Count == 0 ? null : string.Join(", ", notes));
    }

    private static string SanitizeSegment(string segment, out bool changed, out string? reason)
    {
        changed = false;
        reason = null;

        var trimmed = segment.TrimEnd(' ', '.');
        if (!string.Equals(trimmed, segment, StringComparison.Ordinal))
        {
            changed = true;
            reason = "末尾のスペースまたはピリオドを除去";
        }

        if (trimmed.Length == 0)
        {
            return "_";
        }

        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) || Array.IndexOf(InvalidNameChars, ch) >= 0)
            {
                builder.Append('_');
                changed = true;
                reason = "Windows で使用できない文字を置換";
            }
            else
            {
                builder.Append(ch);
            }
        }

        var result = builder.ToString().TrimEnd(' ', '.');
        if (result.Length == 0)
        {
            changed = true;
            reason = "空のファイル名を _ に置換";
            return "_";
        }

        var dotIndex = result.IndexOf('.');
        var baseName = dotIndex >= 0 ? result[..dotIndex] : result;
        if (ReservedDeviceNames.Contains(baseName))
        {
            changed = true;
            reason = "Windows 予約デバイス名をリネーム";
            result = dotIndex >= 0
                ? baseName + "_" + result[dotIndex..]
                : baseName + "_";
        }

        return result;
    }

    public static bool IsUnderRoot(string root, string candidate)
    {
        var rootFull = Path.GetFullPath(root);
        var candidateFull = Path.GetFullPath(candidate);

        if (string.Equals(rootFull, candidateFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        return candidateFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
