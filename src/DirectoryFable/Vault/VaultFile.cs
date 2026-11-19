using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DirectoryFable.Infrastructure;

namespace DirectoryFable.Vault;

/// <summary>
/// KeeSeek's encrypted vault file, built only from .NET's own cryptography:
/// <list type="bullet">
/// <item>the master password gives a 256-bit key through PBKDF2 with HMAC-SHA256 and a random 32-byte salt
/// (600,000 rounds: each guess costs a noticeable fraction of a second);</item>
/// <item>the vault (JSON) is encrypted with AES-256-GCM, a new random 12-byte nonce on every save; GCM's tag also
/// proves that the file is unchanged (a wrong password and a damaged file both fail the tag).</item>
/// </list>
/// Layout: "KSEEKV01" | rounds (UInt32, little-endian) | salt (32) | nonce (12) | tag (16) | encrypted JSON. The
/// first 56 bytes are authenticated too. A save writes a temp file and keeps the previous vault as ".bak". The key
/// stays in memory while the vault is open and is wiped by <see cref="Close"/>. There is no way back from a
/// forgotten master password.
/// </summary>
public sealed class VaultFile
{
    private static readonly byte[] Magic = "KSEEKV01"u8.ToArray();
    private const int Rounds = 600_000;
    private const int SaltSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int HeaderSize = 8 + 4 + SaltSize + NonceSize; // Authenticated, not secret.

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private byte[] _key;
    private byte[] _salt;
    private int _rounds;

    private VaultFile(string path, byte[] key, byte[] salt, int rounds)
    {
        FilePath = path;
        _key = key;
        _salt = salt;
        _rounds = rounds;
    }

    public string FilePath { get; }

    /// <summary>data\keeseek\vault.kseek next to the exe.</summary>
    public static string DefaultPath => Path.Combine(AppSettings.DataFolder, "keeseek", "vault.kseek");

    public static bool Exists(string path) => File.Exists(path);

    /// <summary>A new vault with <see cref="VaultData.NewVault"/>; the key is made on a worker thread.</summary>
    public static async Task<(VaultFile File, VaultData Data)> CreateAsync(string path, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = await Task.Run(() => DeriveKey(password, salt, Rounds));
        var file = new VaultFile(path, key, salt, Rounds);
        var data = VaultData.NewVault();
        file.Save(data);
        return (file, data);
    }

    /// <summary>The vault, or null for a wrong master password. Throws InvalidDataException for a file that is no vault.</summary>
    public static async Task<(VaultFile File, VaultData Data)?> OpenAsync(string path, string password)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length < HeaderSize + TagSize || !bytes.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("This file is no KeeSeek vault.");
        int rounds = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        if (rounds < 10_000 || rounds > 100_000_000)
            throw new InvalidDataException("The vault's header is damaged.");
        byte[] salt = bytes.AsSpan(12, SaltSize).ToArray();
        byte[] key = await Task.Run(() => DeriveKey(password, salt, rounds));

        var nonce = bytes.AsSpan(12 + SaltSize, NonceSize);
        var tag = bytes.AsSpan(HeaderSize, TagSize);
        var cipher = bytes.AsSpan(HeaderSize + TagSize);
        byte[] plain = new byte[cipher.Length];
        try
        {
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plain, bytes.AsSpan(0, HeaderSize));
            var data = JsonSerializer.Deserialize<VaultData>(plain, Json) ?? new VaultData();
            return (new VaultFile(path, key, salt, rounds), data);
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(key);
            return null; // Wrong password (or a changed file).
        }
        catch (JsonException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("The vault opened, but its content is damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Encrypts and writes the vault (a temp file first; the previous vault stays as ".bak").</summary>
    public void Save(VaultData data)
    {
        if (_key.Length != KeySize)
            throw new InvalidOperationException("The vault is closed.");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        try
        {
            byte[] file = new byte[HeaderSize + TagSize + plain.Length];
            Magic.CopyTo(file, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)_rounds);
            _salt.CopyTo(file, 12);
            RandomNumberGenerator.Fill(file.AsSpan(12 + SaltSize, NonceSize));
            using (var aes = new AesGcm(_key, TagSize))
            {
                aes.Encrypt(file.AsSpan(12 + SaltSize, NonceSize), plain, file.AsSpan(HeaderSize + TagSize),
                    file.AsSpan(HeaderSize, TagSize), file.AsSpan(0, HeaderSize));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllBytes(temp, file);
            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, FilePath + ".bak");
            else
                File.Move(temp, FilePath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>A new master password: new salt, new key, saved at once.</summary>
    public async Task ChangePasswordAsync(string password, VaultData data)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = await Task.Run(() => DeriveKey(password, salt, Rounds));
        byte[] oldKey = _key;
        _key = key;
        _salt = salt;
        _rounds = Rounds;
        CryptographicOperations.ZeroMemory(oldKey);
        Save(data);
    }

    /// <summary>Is this the master password? (checked against the file, for changing it).</summary>
    public async Task<bool> IsPasswordAsync(string password)
    {
        byte[] salt = _salt;
        byte[] key = await Task.Run(() => DeriveKey(password, salt, _rounds));
        bool same = CryptographicOperations.FixedTimeEquals(key, _key);
        CryptographicOperations.ZeroMemory(key);
        return same;
    }

    /// <summary>Locked: the key is wiped.</summary>
    public void Close()
    {
        CryptographicOperations.ZeroMemory(_key);
        _key = [];
    }

    private static byte[] DeriveKey(string password, byte[] salt, int rounds)
    {
        byte[] secret = Encoding.UTF8.GetBytes(password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(secret, salt, rounds, HashAlgorithmName.SHA256, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
