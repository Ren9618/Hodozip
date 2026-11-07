using System.Security.Cryptography;

namespace Hodozip.Core;

/// <summary>
/// 圧縮パスワードの生成。暗号論的に安全な乱数だけを使い、文字種の制約を守る。
/// </summary>
public static class PasswordGenerator
{
    public const int MinLength = 12;
    public const int MaxLength = 64;

    private const string LowerChars = "abcdefghijklmnopqrstuvwxyz";
    private const string UpperChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string DigitChars = "0123456789";
    private const string SymbolChars = "!#$%&()*+,-./:;<=>?@[]^_{|}~";

    /// <summary>
    /// 指定された桁数・文字種でパスワードを生成する。
    /// 選ばれた文字種はそれぞれ最低 1 文字含まれる。
    /// </summary>
    public static string Generate(
        int length,
        bool lowerCase = true,
        bool upperCase = true,
        bool digits = true,
        bool symbols = true)
    {
        if (length is < MinLength or > MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"パスワードの桁数は {MinLength}〜{MaxLength} で指定してください。");
        }

        var sets = new List<string>(4);
        if (lowerCase)
        {
            sets.Add(LowerChars);
        }

        if (upperCase)
        {
            sets.Add(UpperChars);
        }

        if (digits)
        {
            sets.Add(DigitChars);
        }

        if (symbols)
        {
            sets.Add(SymbolChars);
        }

        if (sets.Count == 0)
        {
            throw new InvalidOperationException("パスワードに使用する文字種を 1 つ以上選択してください。");
        }

        if (length < sets.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "桁数が選択した文字種の数より少ないため生成できません。");
        }

        var all = string.Concat(sets);
        var buffer = new char[length];
        var position = 0;

        // 各文字種から 1 文字ずつ先に確保する。
        foreach (var set in sets)
        {
            buffer[position++] = set[RandomNumberGenerator.GetInt32(set.Length)];
        }

        for (; position < length; position++)
        {
            buffer[position] = all[RandomNumberGenerator.GetInt32(all.Length)];
        }

        // 確保した文字が常に先頭に固まらないよう、Fisher-Yates で並べ替える。
        for (var index = buffer.Length - 1; index > 0; index--)
        {
            var swap = RandomNumberGenerator.GetInt32(index + 1);
            (buffer[index], buffer[swap]) = (buffer[swap], buffer[index]);
        }

        return new string(buffer);
    }
}
