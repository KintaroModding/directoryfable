using System.Security.Cryptography;
using System.Text;

namespace DirectoryFable.Vault;

/// <summary>Random passwords from Windows' cryptographic random numbers, with at least one character of each chosen kind.</summary>
public static class PasswordGenerator
{
    public const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string Lower = "abcdefghijklmnopqrstuvwxyz";
    public const string Digits = "0123456789";
    public const string Symbols = "!@#$%^&*()-_=+[]{};:,.?/~";

    public static string Generate(int length, bool upper, bool lower, bool digits, bool symbols)
    {
        var kinds = new List<string>();
        if (upper) kinds.Add(Upper);
        if (lower) kinds.Add(Lower);
        if (digits) kinds.Add(Digits);
        if (symbols) kinds.Add(Symbols);
        if (kinds.Count == 0)
            kinds.Add(Lower);
        length = Math.Max(length, kinds.Count);
        string pool = string.Concat(kinds);

        var chars = new char[length];
        // One of each kind first, the rest from the whole pool, then shuffled.
        for (int i = 0; i < kinds.Count; i++)
            chars[i] = kinds[i][RandomNumberGenerator.GetInt32(kinds[i].Length)];
        for (int i = kinds.Count; i < length; i++)
            chars[i] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
        for (int i = length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    /// <summary>
    /// Rough strength in bits: length x log2 of the kinds of characters in it (a generated password reaches this;
    /// a word with digits stays below it in reality).
    /// </summary>
    public static double Bits(string password)
    {
        if (password.Length == 0)
            return 0;
        int pool = 0;
        if (password.Any(char.IsAsciiLetterUpper)) pool += 26;
        if (password.Any(char.IsAsciiLetterLower)) pool += 26;
        if (password.Any(char.IsAsciiDigit)) pool += 10;
        if (password.Any(c => !char.IsAsciiLetterOrDigit(c))) pool += 33;
        return password.Length * Math.Log2(Math.Max(pool, 2));
    }

    /// <summary>"Weak", "Fair", "Strong", "Very strong" for the bits.</summary>
    public static string Rating(double bits) => bits switch
    {
        < 45 => "Weak",
        < 70 => "Fair",
        < 100 => "Strong",
        _ => "Very strong",
    };
}
