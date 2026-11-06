namespace Hodozip.Core;

/// <summary>既存ファイルが見つかったときの上書き方針。</summary>
public enum OverwritePolicy
{
    /// <summary>UI に問い合わせる。</summary>
    Ask,

    /// <summary>常に上書きする。</summary>
    Overwrite,

    /// <summary>常にスキップする。</summary>
    Skip,

    /// <summary>既存ファイルを残し、新しいファイル名に番号を付ける。</summary>
    AutoRename,
}

/// <summary>Ask 方針で UI が返す判断。</summary>
public enum OverwriteDecision
{
    Overwrite,
    Skip,
    AutoRename,
}
