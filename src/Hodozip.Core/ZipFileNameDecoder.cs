using System.Buffers.Binary;
using System.Text;

namespace Hodozip.Core;

/// <summary>ZIP ファイル名のデコード方法。</summary>
public enum ZipFileNameEncoding
{
    /// <summary>UTF-8 フラグ・Unicode 拡張フィールドを優先し、残りは自動判定する。</summary>
    Auto,

    /// <summary>フラグのないファイル名を UTF-8 として読む。</summary>
    Utf8,

    /// <summary>フラグのないファイル名を Shift_JIS (CP932) として読む。</summary>
    ShiftJis,

    /// <summary>フラグのないファイル名を GBK (CP936) として読む。</summary>
    Gbk,
}

/// <summary>
/// ZIP セントラルディレクトリを直接読み、ファイル名だけを Hodozip の判定でデコードする。
/// SharpCompress は UTF-8 フラグのない名前を一律 CP932 で読むため、GBK 名の文字化けを防ぐ。
/// 解析に失敗した場合は null を返し、呼び出し側は従来の SharpCompress の名前へフォールバックする。
/// </summary>
internal static class ZipFileNameDecoder
{
    private const ushort Utf8Flag = 0x0800;
    private const ushort UnicodePathExtraFieldId = 0x7075;
    private const uint CentralDirectoryHeaderSignature = 0x02014B50;
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064B50;
    private const int EndOfCentralDirectorySize = 22;
    private const int Zip64EndOfCentralDirectorySize = 56;
    private const int Zip64EndOfCentralDirectoryLocatorSize = 20;
    private const int MaxZipCommentSize = 65535;
    private const int CentralDirectoryHeaderSize = 46;

    // スコアの重み。GBK バイト列を CP932 と誤読したときに出る症状を強く減点する。
    private const int PrivateUsePenalty = 40;
    private const int ReplacementPenalty = 40;
    private const int HalfwidthKatakanaPenalty = 8;
    private const int KanaReward = 4;
    private const int MaxKanaReward = 40;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly Encoding Cp932;
    private static readonly Encoding Gbk;

    static ZipFileNameDecoder()
    {
        EncodingSetup.EnsureRegistered();
        Cp932 = CreateEncoding(932);
        Gbk = CreateEncoding(936);
    }

    /// <summary>
    /// セントラルディレクトリ順にデコードしたファイル名を返す。
    /// ZIP として解析できない場合や、機能不足の形式の場合は null。
    /// </summary>
    public static IReadOnlyList<string>? TryDecodeEntryNames(string archivePath, ZipFileNameEncoding mode)
    {
        try
        {
            var path = LongPath.ToFileSystemPath(archivePath);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var entries = ReadCentralDirectory(stream);
            if (entries is null)
            {
                return null;
            }

            var names = new string[entries.Count];
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                names[index] = DecodeEntryName(
                    entry.NameBytes,
                    entry.Flags,
                    entry.ExtraBytes,
                    mode);
            }

            return names;
        }
        catch (Exception)
        {
            // 名前の補正はベストエフォート。失敗しても解凍自体は従来経路で続ける。
            return null;
        }
    }

    /// <summary>フラグなしファイル名の自動判定。テスト用に公開している。</summary>
    internal static ZipFileNameEncoding DetectEncoding(ReadOnlySpan<byte> nameBytes)
    {
        if (IsValidUtf8(nameBytes))
        {
            return ZipFileNameEncoding.Utf8;
        }

        var cp932 = Cp932.GetString(nameBytes);
        var gbk = Gbk.GetString(nameBytes);

        // 半角カタカナと ASCII だけで構成された名前は、CP932 の自然な名前とみなす。
        // GBK のバイト列がたまたま半角カタカナだけになることは実質ない。
        if (HasHalfwidthKatakana(cp932) && IsAsciiAndHalfwidthKatakanaOnly(cp932))
        {
            return ZipFileNameEncoding.ShiftJis;
        }

        return Score(gbk) > Score(cp932) ? ZipFileNameEncoding.Gbk : ZipFileNameEncoding.ShiftJis;
    }

    private static bool HasHalfwidthKatakana(string value)
    {
        foreach (var ch in value)
        {
            if (ch is >= '\uFF61' and <= '\uFF9F')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAsciiAndHalfwidthKatakanaOnly(string value)
    {
        foreach (var ch in value)
        {
            if (ch >= 0x80 && ch is not (>= '\uFF61' and <= '\uFF9F'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>1 エントリ分のファイル名をデコードする。テスト用に公開している。</summary>
    internal static string DecodeEntryName(
        ReadOnlySpan<byte> nameBytes,
        ushort flags,
        ReadOnlySpan<byte> extraBytes,
        ZipFileNameEncoding mode)
    {
        // 1. UTF-8 フラグありは現行どおり UTF-8。手動指定より優先する。
        if ((flags & Utf8Flag) != 0)
        {
            return Utf8.GetString(nameBytes);
        }

        // 2. 手動指定はフラグなしエントリ全体に適用する（自動判定の逃げ道）。
        if (mode != ZipFileNameEncoding.Auto)
        {
            return mode switch
            {
                ZipFileNameEncoding.Utf8 => Utf8.GetString(nameBytes),
                ZipFileNameEncoding.ShiftJis => Cp932.GetString(nameBytes),
                ZipFileNameEncoding.Gbk => Gbk.GetString(nameBytes),
                _ => DecodeAuto(nameBytes),
            };
        }

        // 3. 自動判定では Info-ZIP Unicode Path Extra Field (0x7075) を優先する。
        if (TryGetUnicodePath(extraBytes, nameBytes, out var unicodePath))
        {
            return unicodePath;
        }

        // 4. フラグなしの自動判定。
        return DecodeAuto(nameBytes);
    }

    private static string DecodeAuto(ReadOnlySpan<byte> nameBytes)
    {
        // フラグなし UTF-8 の救済。厳密に妥当な場合だけ UTF-8 とみなす。
        if (IsValidUtf8(nameBytes))
        {
            return Utf8.GetString(nameBytes);
        }

        var cp932 = Cp932.GetString(nameBytes);
        var gbk = Gbk.GetString(nameBytes);

        // 半角カタカナと ASCII だけで構成された名前は、CP932 の自然な名前とみなす。
        // GBK のバイト列がたまたま半角カタカナだけになることは実質ない。
        if (HasHalfwidthKatakana(cp932) && IsAsciiAndHalfwidthKatakanaOnly(cp932))
        {
            return cp932;
        }

        // 同点・判定不能は日本語環境の安全側として CP932。
        return Score(gbk) > Score(cp932) ? gbk : cp932;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int Score(string value)
    {
        var score = 0;
        var halfwidthKatakana = 0;
        var cjkIdeographs = 0;
        var kana = 0;
        var hasPrivateUse = false;
        var hasReplacement = false;
        var hasFullwidthKatakanaMiddleDot = false;

        foreach (var ch in value)
        {
            if (ch is >= '\uE000' and <= '\uF8FF')
            {
                score -= PrivateUsePenalty;
                hasPrivateUse = true;
            }
            else if (ch == '\uFFFD')
            {
                score -= ReplacementPenalty;
                hasReplacement = true;
            }
            else if (ch is >= '\uFF61' and <= '\uFF9F')
            {
                halfwidthKatakana++;
            }
            else if (ch == '\u30FB')
            {
                hasFullwidthKatakanaMiddleDot = true;
            }
            else if (ch is (>= '\u3041' and <= '\u3096') or (>= '\u30A1' and <= '\u30FA'))
            {
                kana++;
            }
            else if (ch is >= '\u4E00' and <= '\u9FFF')
            {
                cjkIdeographs++;
            }
        }

        // ひらがな・全角カタカナが自然に読める結果を加点する。
        score += Math.Min(kana * KanaReward, MaxKanaReward);

        // 半角カタカナ自体は正当な日本語名でも使われるため、GBK 誤読の兆候
        // （私用領域・置換文字・全角中黒・漢字との不自然な混在）がある場合だけ減点する。
        var unnaturalHalfwidth = halfwidthKatakana > 0
            && (hasPrivateUse
                || hasReplacement
                || hasFullwidthKatakanaMiddleDot
                || (cjkIdeographs > 0 && halfwidthKatakana > cjkIdeographs));
        if (unnaturalHalfwidth)
        {
            score -= halfwidthKatakana * HalfwidthKatakanaPenalty;
        }

        return score;
    }

    private static bool TryGetUnicodePath(ReadOnlySpan<byte> extraBytes, ReadOnlySpan<byte> rawName, out string unicodePath)
    {
        unicodePath = string.Empty;
        var offset = 0;

        while (offset + 4 <= extraBytes.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extraBytes[offset..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extraBytes[(offset + 2)..]);
            offset += 4;
            if (offset + size > extraBytes.Length)
            {
                return false;
            }

            if (id == UnicodePathExtraFieldId && size >= 5)
            {
                var data = extraBytes.Slice(offset, size);

                // version 1 + 元の名前の CRC32 + UTF-8 名。CRC が合わないものは無視する。
                if (data[0] == 1
                    && BinaryPrimitives.ReadUInt32LittleEndian(data[1..]) == ComputeCrc32(rawName))
                {
                    try
                    {
                        unicodePath = StrictUtf8.GetString(data[5..]);
                        return true;
                    }
                    catch (DecoderFallbackException)
                    {
                        // 壊れた Unicode フィールドは無視して次の手段へ。
                    }
                }
            }

            offset += size;
        }

        return false;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static Encoding CreateEncoding(int codePage)
    {
        return Encoding.GetEncoding(
            codePage,
            EncoderFallback.ReplacementFallback,
            new DecoderReplacementFallback("\uFFFD"));
    }

    private static List<CentralEntry>? ReadCentralDirectory(Stream stream)
    {
        if (stream.Length < EndOfCentralDirectorySize)
        {
            return null;
        }

        var searchLength = (int)Math.Min(stream.Length, EndOfCentralDirectorySize + MaxZipCommentSize);
        var search = new byte[searchLength];
        stream.Position = stream.Length - searchLength;
        stream.ReadExactly(search);

        var eocdOffsetInSearch = -1;
        for (var index = search.Length - EndOfCentralDirectorySize; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(search.AsSpan(index)) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(search.AsSpan(index + 20));
            if (index + EndOfCentralDirectorySize + commentLength == search.Length)
            {
                eocdOffsetInSearch = index;
                break;
            }
        }

        if (eocdOffsetInSearch < 0)
        {
            return null;
        }

        var eocd = search.AsSpan(eocdOffsetInSearch);
        var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]);
        var centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]);
        if (diskNumber != 0 || centralDirectoryDisk != 0)
        {
            // 分割 ZIP は SharpCompress の従来経路に任せる。
            return null;
        }

        var entryCount = (ulong)BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        ulong centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        ulong centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        var eocdFileOffset = stream.Length - searchLength + eocdOffsetInSearch;

        var needsZip64 = entryCount == ushort.MaxValue
            || centralDirectorySize == uint.MaxValue
            || centralDirectoryOffset == uint.MaxValue;

        if (needsZip64)
        {
            if (!TryReadZip64EndOfCentralDirectory(
                    stream,
                    eocdFileOffset,
                    out entryCount,
                    out centralDirectorySize,
                    out centralDirectoryOffset))
            {
                return null;
            }
        }

        if (entryCount > int.MaxValue
            || centralDirectoryOffset > (ulong)stream.Length
            || centralDirectorySize > (ulong)stream.Length
            || centralDirectoryOffset + centralDirectorySize > (ulong)stream.Length)
        {
            return null;
        }

        stream.Position = (long)centralDirectoryOffset;
        var entries = new List<CentralEntry>((int)entryCount);
        Span<byte> header = stackalloc byte[CentralDirectoryHeaderSize];

        for (ulong index = 0; index < entryCount; index++)
        {
            stream.ReadExactly(header);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralDirectoryHeaderSignature)
            {
                return null;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);

            var nameBytes = new byte[nameLength];
            stream.ReadExactly(nameBytes);

            var extraBytes = new byte[extraLength];
            stream.ReadExactly(extraBytes);

            if (commentLength > 0)
            {
                stream.Seek(commentLength, SeekOrigin.Current);
            }

            entries.Add(new CentralEntry(flags, nameBytes, extraBytes));
        }

        return entries;
    }

    private static bool TryReadZip64EndOfCentralDirectory(
        Stream stream,
        long eocdFileOffset,
        out ulong entryCount,
        out ulong centralDirectorySize,
        out ulong centralDirectoryOffset)
    {
        entryCount = 0;
        centralDirectorySize = 0;
        centralDirectoryOffset = 0;

        var locatorOffset = eocdFileOffset - Zip64EndOfCentralDirectoryLocatorSize;
        if (locatorOffset < 0)
        {
            return false;
        }

        Span<byte> locator = stackalloc byte[Zip64EndOfCentralDirectoryLocatorSize];
        stream.Position = locatorOffset;
        stream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64EndOfCentralDirectoryLocatorSignature)
        {
            return false;
        }

        var zip64Offset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
        if (zip64Offset > (ulong)stream.Length - Zip64EndOfCentralDirectorySize)
        {
            return false;
        }

        Span<byte> record = stackalloc byte[Zip64EndOfCentralDirectorySize];
        stream.Position = (long)zip64Offset;
        stream.ReadExactly(record);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfCentralDirectorySignature)
        {
            return false;
        }

        var diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        var centralDirectoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
        if (diskNumber != 0 || centralDirectoryDisk != 0)
        {
            return false;
        }

        entryCount = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        centralDirectorySize = BinaryPrimitives.ReadUInt64LittleEndian(record[40..]);
        centralDirectoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(record[48..]);
        return true;
    }

    private sealed record CentralEntry(ushort Flags, byte[] NameBytes, byte[] ExtraBytes);
}
