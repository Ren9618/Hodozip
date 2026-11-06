using System.Text.Json;
using System.Text.Json.Serialization;
using Hodozip.Core;

namespace Hodozip.Services;

public sealed class AppSettings
{
    /// <summary>Phase 1 は「アーカイブと同じ場所」固定。</summary>
    public string DestinationRule { get; set; } = "SameAsArchive";

    public bool OpenFolderAfterExtraction { get; set; }

    public bool CloseAfterAutoExtract { get; set; }

    /// <summary>解凍先フォルダが既に存在するときの扱い。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FolderCollisionPolicy FolderCollisionPolicy { get; set; } = FolderCollisionPolicy.Ask;

    /// <summary>同名ファイルが既に存在するときの扱い。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverwritePolicy OverwritePolicy { get; set; } = OverwritePolicy.Ask;
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
