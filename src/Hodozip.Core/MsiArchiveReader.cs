using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Hodozip.Core;

internal sealed record MsiFileRecord(
    string Key,
    string? FileName,
    long Size,
    int Sequence,
    string? Component);

internal sealed record MsiMediaRecord(
    int LastSequence,
    string? Cabinet);

internal sealed record MsiComponentRecord(
    string Key,
    string? Directory);

internal sealed record MsiDirectoryRecord(
    string Key,
    string? Parent,
    string? DefaultDir);

/// <summary>
/// Windows Installer データベース (msi.dll) から File / Media / Directory テーブルと
/// 埋め込みストリームを読み取る。UI 非依存。
/// </summary>
internal sealed class MsiArchiveReader : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;

    private IntPtr _database;

    private MsiArchiveReader(IntPtr database)
    {
        _database = database;
    }

    public static MsiArchiveReader Open(string path)
    {
        var status = MsiOpenDatabaseW(path, IntPtr.Zero, out var database);
        if (status != ErrorSuccess)
        {
            throw new InvalidDataException($"MSI データベースを開けませんでした（エラー {status}）。");
        }

        return new MsiArchiveReader(database);
    }

    public IReadOnlyList<MsiFileRecord> ReadFiles()
    {
        EnsureOpen();

        var rows = Query(
            "SELECT `File`, `FileName`, `FileSize`, `Sequence`, `Component_` FROM `File`",
            fieldCount: 5);

        var files = new List<MsiFileRecord>(rows.Count);
        foreach (var row in rows)
        {
            var key = row[0];
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            files.Add(new MsiFileRecord(
                key,
                row[1],
                ParseInt64(row[2]),
                ParseInt32(row[3]),
                row[4]));
        }

        return files;
    }

    public IReadOnlyList<MsiMediaRecord> ReadMedia()
    {
        EnsureOpen();

        var rows = Query(
            "SELECT `LastSequence`, `Cabinet` FROM `Media`",
            fieldCount: 2,
            allowMissingTable: true);

        var media = new List<MsiMediaRecord>(rows.Count);
        foreach (var row in rows)
        {
            media.Add(new MsiMediaRecord(ParseInt32(row[0]), row[1]));
        }

        return media;
    }

    public IReadOnlyList<MsiComponentRecord> ReadComponents()
    {
        EnsureOpen();

        var rows = Query(
            "SELECT `Component`, `Directory_` FROM `Component`",
            fieldCount: 2,
            allowMissingTable: true);

        var components = new List<MsiComponentRecord>(rows.Count);
        foreach (var row in rows)
        {
            var key = row[0];
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            components.Add(new MsiComponentRecord(key, row[1]));
        }

        return components;
    }

    public IReadOnlyList<MsiDirectoryRecord> ReadDirectories()
    {
        EnsureOpen();

        var rows = Query(
            "SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`",
            fieldCount: 3);

        var directories = new List<MsiDirectoryRecord>(rows.Count);
        foreach (var row in rows)
        {
            var key = row[0];
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            directories.Add(new MsiDirectoryRecord(key, row[1], row[2]));
        }

        return directories;
    }

    public IReadOnlyList<string> ReadStreamNames()
    {
        EnsureOpen();

        var rows = Query("SELECT `Name` FROM `_Streams`", fieldCount: 1, allowMissingTable: true);
        var names = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row[0]))
            {
                names.Add(row[0]!);
            }
        }

        return names;
    }

    /// <summary>
    /// _Streams テーブルのストリームをファイルへ書き出す。見つからない場合は false。
    /// </summary>
    public bool TryExportStream(string streamName, string destinationPath, CancellationToken cancellationToken)
    {
        EnsureOpen();

        var escapedName = streamName.Replace("'", "''", StringComparison.Ordinal);
        var sql = $"SELECT `Data` FROM `_Streams` WHERE `Name` = '{escapedName}'";

        var status = MsiDatabaseOpenViewW(_database, sql, out var view);
        if (status != ErrorSuccess)
        {
            throw CreateException("MSI のストリームを準備できませんでした", status);
        }

        try
        {
            status = MsiViewExecute(view, IntPtr.Zero);
            if (status != ErrorSuccess)
            {
                throw CreateException("MSI のストリームを読み取れませんでした", status);
            }

            status = MsiViewFetch(view, out var record);
            if (status == ErrorNoMoreItems)
            {
                return false;
            }

            if (status != ErrorSuccess)
            {
                throw CreateException("MSI のストリームを取得できませんでした", status);
            }

            try
            {
                using var output = new FileStream(
                    LongPath.ToFileSystemPath(destinationPath),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.SequentialScan);

                var buffer = new byte[81920];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var length = (uint)buffer.Length;
                    status = MsiRecordReadStream(record, 1, buffer, ref length);
                    if (status != ErrorSuccess && status != ErrorMoreData)
                    {
                        throw CreateException("MSI のストリームを展開できませんでした", status);
                    }

                    if (length == 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, (int)length);
                }
            }
            finally
            {
                MsiCloseHandle(record);
            }
        }
        finally
        {
            MsiCloseHandle(view);
        }

        return true;
    }

    public void Dispose()
    {
        if (_database != IntPtr.Zero)
        {
            MsiCloseHandle(_database);
            _database = IntPtr.Zero;
        }
    }

    private List<string?[]> Query(string sql, int fieldCount, bool allowMissingTable = false)
    {
        EnsureOpen();

        var status = MsiDatabaseOpenViewW(_database, sql, out var view);
        if (status != ErrorSuccess)
        {
            if (allowMissingTable)
            {
                return new List<string?[]>();
            }

            throw CreateException("MSI のテーブルを準備できませんでした", status);
        }

        try
        {
            status = MsiViewExecute(view, IntPtr.Zero);
            if (status != ErrorSuccess)
            {
                if (allowMissingTable)
                {
                    return new List<string?[]>();
                }

                throw CreateException("MSI のテーブルを読み取れませんでした", status);
            }

            var rows = new List<string?[]>();
            while (true)
            {
                status = MsiViewFetch(view, out var record);
                if (status == ErrorNoMoreItems)
                {
                    break;
                }

                if (status != ErrorSuccess)
                {
                    throw CreateException("MSI のテーブルを取得できませんでした", status);
                }

                try
                {
                    var values = new string?[fieldCount];
                    for (var index = 0; index < fieldCount; index++)
                    {
                        values[index] = GetString(record, (uint)(index + 1));
                    }

                    rows.Add(values);
                }
                finally
                {
                    MsiCloseHandle(record);
                }
            }

            return rows;
        }
        finally
        {
            MsiCloseHandle(view);
        }
    }

    private static string? GetString(IntPtr record, uint field)
    {
        var capacity = 256u;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var builder = new StringBuilder((int)capacity);
            var size = capacity;
            var status = MsiRecordGetStringW(record, field, builder, ref size);

            if (status == ErrorSuccess)
            {
                return builder.ToString();
            }

            if (status == ErrorMoreData)
            {
                capacity = size > capacity ? size : capacity * 2;
                continue;
            }

            // null の整数フィールドなどは空文字として扱う。
            if (status == 87)
            {
                return null;
            }

            throw CreateException("MSI の値を読み取れませんでした", status);
        }

        throw new InvalidDataException("MSI の文字列が長すぎます。");
    }

    private static int ParseInt32(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static long ParseInt64(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static InvalidDataException CreateException(string message, uint status)
    {
        return new InvalidDataException($"{message}（エラー {status}）。");
    }

    private void EnsureOpen()
    {
        if (_database == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(MsiArchiveReader));
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiOpenDatabaseW(string databasePath, IntPtr persist, out IntPtr database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordGetStringW(
        IntPtr record,
        uint field,
        StringBuilder? valueBuffer,
        ref uint valueBufferLength);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiRecordReadStream(
        IntPtr record,
        uint field,
        [Out] byte[] dataBuffer,
        ref uint dataBufferLength);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(IntPtr handle);
}
