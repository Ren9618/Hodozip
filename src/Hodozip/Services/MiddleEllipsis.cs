namespace Hodozip.Services;

/// <summary>固定幅のウィンドウでパスやファイル名を中間省略する。</summary>
internal static class MiddleEllipsis
{
    public const char Ellipsis = '…';

    public static string CompactMiddle(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0 || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        if (maxLength <= 3)
        {
            return value[..maxLength];
        }

        var left = (maxLength - 1) / 2;
        var right = maxLength - 1 - left;
        return string.Concat(value[..left], Ellipsis.ToString(), value[^right..]);
    }
}
