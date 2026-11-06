namespace Hodozip.Core;

/// <summary>
/// 260 文字を超えるパスでも Windows API が扱えるよう \\?\ を付与する。
/// 表示用のパスには使わないこと。
/// </summary>
internal static class LongPath
{
    private const int ExtendedPrefixThreshold = 240;

    public static string ToFileSystemPath(string path)
    {
        if (path.Length < ExtendedPrefixThreshold)
        {
            return path;
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return @"\\?\" + path;
    }

    public static bool FileExists(string path) => File.Exists(ToFileSystemPath(path));

    public static bool DirectoryExists(string path) => Directory.Exists(ToFileSystemPath(path));

    public static void CreateDirectory(string path) => Directory.CreateDirectory(ToFileSystemPath(path));

    public static void DeleteFile(string path)
    {
        var target = ToFileSystemPath(path);
        if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    public static void MoveFile(string source, string destination, bool overwrite)
    {
        File.Move(ToFileSystemPath(source), ToFileSystemPath(destination), overwrite);
    }

    public static void SetLastWriteTime(string path, DateTime value)
    {
        File.SetLastWriteTime(ToFileSystemPath(path), value);
    }
}
