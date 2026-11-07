namespace Hodozip.Core;

/// <summary>
/// 起動時の更新確認を実行するかどうかの判定。UI やファイル システムに依存しない
/// 純粋なロジックとして切り出し、単体テストで検証できるようにする。
/// </summary>
public static class UpdatePolicy
{
    /// <summary>更新確認の最小間隔（1 日 1 回）。</summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// 更新確認を実行すべきか。無効化されている場合、または前回の確認から
    /// <paramref name="interval"/> 未満しか経過していない場合は false。
    /// </summary>
    public static bool ShouldCheck(
        bool checkEnabled,
        DateTimeOffset? lastCheckUtc,
        DateTimeOffset now,
        TimeSpan? interval = null)
    {
        if (!checkEnabled)
        {
            return false;
        }

        var effectiveInterval = interval ?? DefaultCheckInterval;
        return lastCheckUtc is null || now - lastCheckUtc.Value >= effectiveInterval;
    }

    /// <summary>
    /// ユーザーが「このバージョンをスキップ」したリリースかどうか。
    /// 保存されたバージョンが対象リリース以上なら、そのリリースは通知しない。
    /// </summary>
    public static bool IsVersionSkipped(ReleaseVersion release, string? skippedVersion)
    {
        return !string.IsNullOrWhiteSpace(skippedVersion)
            && ReleaseVersion.TryParse(skippedVersion, out var skipped)
            && skipped.CompareTo(release) >= 0;
    }
}
