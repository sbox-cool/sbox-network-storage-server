using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SboxNetworkStorage.Server.Owner;

public static class OwnerTotp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var result = new StringBuilder(32);
        int bits = 0, buffer = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5) { bits -= 5; result.Append(Alphabet[(buffer >> bits) & 31]); }
        }
        return result.ToString();
    }

    public static string Code(string secret, long step)
    {
        var key = new byte[secret.Length * 5 / 8];
        int bits = 0, buffer = 0, index = 0;
        foreach (var value in secret)
        {
            var digit = Alphabet.IndexOf(value);
            if (digit < 0) throw new InvalidDataException("Invalid authenticator secret.");
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8) { bits -= 8; key[index++] = (byte)(buffer >> bits); }
        }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(key, counter);
        CryptographicOperations.ZeroMemory(key);
        var offset = hash[^1] & 15;
        var value32 = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (value32 % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static long? Match(string secret, string? code, long lastStep, DateTimeOffset now)
    {
        if (code is not { Length: 6 } || !code.All(char.IsAsciiDigit)) return null;
        var step = now.ToUnixTimeSeconds() / 30;
        for (var candidate = step - 1; candidate <= step + 1; candidate++)
            if (candidate > lastStep && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, candidate)), Encoding.ASCII.GetBytes(code))) return candidate;
        return null;
    }

    public static string RecoveryHash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));
}
