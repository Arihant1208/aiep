using System.Buffers.Binary;

namespace Aiep.Application.Ids;

/// <summary>
/// Crockford base32 (0-9, A-Z without I, L, O, U): no padding, unambiguous when read aloud or retyped.
/// Used for resource IDs (uppercase) and API key parts (lowercase).
/// </summary>
public static class CrockfordBase32
{
    private const string Upper = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string Lower = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>Encodes 128 bits as exactly 26 characters (the first one is 0-7).</summary>
    public static string Encode128(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        var number = BinaryPrimitives.ReadUInt128BigEndian(bytes);
        Span<char> chars = stackalloc char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = Upper[(int)(number & 31)];
            number >>= 5;
        }

        return new string(chars);
    }

    /// <summary>Decodes 26 characters (case-insensitive) back into 128 bits. Rejects anything else.</summary>
    public static bool TryDecode128(ReadOnlySpan<char> text, out Guid value)
    {
        value = Guid.Empty;
        if (text.Length != 26)
        {
            return false;
        }

        UInt128 number = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var digit = Upper.IndexOf(char.ToUpperInvariant(text[i]));
            if (digit < 0 || (i == 0 && digit > 7))
            {
                return false;
            }

            number = (number << 5) | (uint)digit;
        }

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, number);
        value = new Guid(bytes, bigEndian: true);
        return true;
    }

    /// <summary>Encodes arbitrary bytes in lowercase, 5 bits per character, final group zero-padded.</summary>
    public static string EncodeLower(ReadOnlySpan<byte> data)
    {
        var chars = new char[((data.Length * 8) + 4) / 5];
        int buffer = 0, bits = 0, index = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                chars[index++] = Lower[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            chars[index] = Lower[(buffer << (5 - bits)) & 31];
        }

        return new string(chars);
    }
}
