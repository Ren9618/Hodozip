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

    /// <summary>解凍セッション全体を中止する。</summary>
    Cancel,
}

/// <summary>解凍先フォルダが既に存在するときの扱い。</summary>
public enum FolderCollisionPolicy
{
    /// <summary>解凍開始前に UI に問い合わせる。</summary>
    Ask,

    /// <summary>既存フォルダへ統合して解凍する。</summary>
    Merge,

    /// <summary>連番を付けた新しいフォルダを作る（Phase 2 までの挙動）。</summary>
    AutoRename,
}

/// <summary>フォルダ衝突時に UI が返す判断。</summary>
public enum FolderCollisionDecision
{
    Merge,
    AutoRename,
    Cancel,
}

/// <summary>フォルダ衝突の確認に必要な情報。</summary>
public sealed record FolderCollisionConflict(
    string FolderName,
    string FolderPath,
    string BaseDirectory,
    bool ExistingIsDirectory);
