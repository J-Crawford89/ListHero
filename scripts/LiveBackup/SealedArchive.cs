using System.Security.Cryptography;
using ListHero.Infrastructure.Maintenance;

namespace ListHero.LiveBackup;

// Encrypt-then-MAC: authenticate the complete header/ciphertext before creating plaintext.
// Streaming avoids holding a database export in memory. This is a versioned application format.
public static class SealedArchive
{
    private static readonly byte[] Magic = "LHBACK01"u8.ToArray();
    public const long MaximumBytes = 64L * 1024 * 1024 * 1024;
    public static void ValidatePassphrase(string passphrase)
    {
        if (passphrase.Length is < 16 or > 1024) throw new ArgumentException();
    }

    public static void Seal(string source, string target, string passphrase)
    {
        ValidatePassphrase(passphrase);
        using var input = File.OpenRead(source);
        if (input.Length is < 1 or > MaximumBytes - 88) throw new InvalidDataException();
        var header = new byte[40]; Magic.CopyTo(header, 0); RandomNumberGenerator.Fill(header.AsSpan(8));
        var keys = Rfc2898DeriveBytes.Pbkdf2(passphrase, header.AsSpan(8, 16), 600_000, HashAlgorithmName.SHA256, 64);
        try
        {
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            output.Write(header);
            using (var aes = Aes.Create())
            {
                aes.Key = keys[..32]; aes.IV = header[24..];
                using var encryptor = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, true);
                input.CopyTo(encryptor); encryptor.FlushFinalBlock();
            }
            var length = output.Position; output.Position = 0;
            var tag = Authenticate(output, length, keys[32..]);
            output.Write(tag); output.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(keys); }
    }

    public static void Open(string source, string target, string passphrase)
    {
        ValidatePassphrase(passphrase);
        using var input = File.OpenRead(source);
        if (input.Length is < 88 or > MaximumBytes || (input.Length - 72) % 16 != 0) throw new InvalidDataException();
        var header = new byte[40]; input.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic)) throw new InvalidDataException();
        var keys = Rfc2898DeriveBytes.Pbkdf2(passphrase, header.AsSpan(8, 16), 600_000, HashAlgorithmName.SHA256, 64);
        try
        {
            input.Position = 0;
            var actual = Authenticate(input, input.Length - 32, keys[32..]);
            var expected = new byte[32]; input.ReadExactly(expected);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new CryptographicException();
            input.Position = 40;
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var aes = Aes.Create(); aes.Key = keys[..32]; aes.IV = header[24..];
            using var decryptor = new CryptoStream(output, aes.CreateDecryptor(), CryptoStreamMode.Write, true);
            CopyExactly(input, decryptor, input.Length - 72);
            decryptor.FlushFinalBlock(); output.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(keys); }
    }

    private static byte[] Authenticate(Stream input, long length, byte[] key)
    {
        try
        {
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            var buffer = new byte[81920];
            while (length > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                if (read == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, read); length -= read;
            }
            return hash.GetHashAndReset();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static void CopyExactly(Stream source, Stream target, long length)
    {
        var buffer = new byte[81920];
        while (length > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read == 0) throw new EndOfStreamException();
            target.Write(buffer, 0, read); length -= read;
        }
    }
}
