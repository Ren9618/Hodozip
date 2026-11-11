using System.Text.Json;
using System.Text.Json.Serialization;
using Hodozip.Core;

namespace Hodozip.Services;

/// <summary>解凍先・圧縮先の選び方。</summary>
public enum DestinationSelectionMode
{
    SameAsArchive,
    FixedFolder,
    AskEveryTime,
}

/// <summary>完了時の通知音。</summary>
public enum NotificationSoundMode
{
    None,
    System,
    File,
}

public sealed class AppSettings
{
    /// <summary>旧バージョンとの互換用。読み込み時は無視する。</summary>
    public string DestinationRule { get; set; } = "SameAsArchive";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DestinationSelectionMode ExtractionDestination { get; set; } = DestinationSelectionMode.SameAsArchive;

    public string? ExtractionDestinationFolder { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DestinationSelectionMode CompressionDestination { get; set; } = DestinationSelectionMode.SameAsArchive;

    public string? CompressionDestinationFolder { get; set; }

    /// <summary>確認モードでも、既存ファイルの方が新しい場合は確認せずスキップする。</summary>
    public bool SkipExistingIfNewer { get; set; }

    /// <summary>macOS 由来の不要ファイルを除外する（既定 ON）。</summary>
    public bool ExcludeMacOsMetadata { get; set; } = true;

    /// <summary>ファイル名を NFC に正規化する（既定 ON）。</summary>
    public bool NormalizeFileNamesNfc { get; set; } = true;

    /// <summary>ZIP の UTF-8 フラグなしファイル名の文字コード（既定は自動判定）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ZipFileNameEncoding ZipFileNameEncoding { get; set; } = ZipFileNameEncoding.Auto;

    /// <summary>完了時にトースト通知を出す（既定 OFF）。</summary>
    public bool NotifyOnCompletion { get; set; }

    /// <summary>進捗ウィンドウを閉じても処理を続ける（既定 OFF）。</summary>
    public bool RunInBackground { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NotificationSoundMode NotificationSound { get; set; } = NotificationSoundMode.None;

    public string? NotificationSoundPath { get; set; }

    /// <summary>圧縮パスワードの生成設定。</summary>
    public int GeneratedPasswordLength { get; set; } = 20;

    public bool GeneratedPasswordLowerCase { get; set; } = true;

    public bool GeneratedPasswordUpperCase { get; set; } = true;

    public bool GeneratedPasswordDigits { get; set; } = true;

    public bool GeneratedPasswordSymbols { get; set; } = true;

    public bool OpenFolderAfterExtraction { get; set; }

    public bool CloseAfterAutoExtract { get; set; }

    /// <summary>解凍先フォルダが既に存在するときの扱い。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FolderCollisionPolicy FolderCollisionPolicy { get; set; } = FolderCollisionPolicy.Ask;

    /// <summary>同名ファイルが既に存在するときの扱い。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverwritePolicy OverwritePolicy { get; set; } = OverwritePolicy.Ask;

    /// <summary>UI 言語。変更は次回起動時に反映される。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppLanguage Language { get; set; } = AppLanguage.System;

    /// <summary>ZIP 並列解凍の最大並列度。0 は CPU コア数に合わせる自動。</summary>
    public int MaxDegreeOfParallelism { get; set; }

    /// <summary>起動時に GitHub Releases を確認する（既定 ON）。</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>最後に更新確認を試みた日時（UTC）。1 日 1 回の制限に使う。</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>ユーザーが「このバージョンをスキップ」した更新。</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>前回の圧縮先フォルダー。</summary>
    public string? LastCompressionDirectory { get; set; }
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hodozip");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // 設定保存の失敗はアプリの動作を止めない。
        }
    }
}
