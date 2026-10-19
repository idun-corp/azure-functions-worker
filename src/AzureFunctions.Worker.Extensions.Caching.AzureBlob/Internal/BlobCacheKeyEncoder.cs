using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;

/// <summary>
/// Maps a cache key onto a blob name, and recovers it from entry metadata.
/// </summary>
/// <remarks>
/// Percent-escaping every character outside the unreserved set is what keeps prefix search exact:
/// the encoding is per character, so Encode(key) starts with Encode(prefix) exactly when key starts
/// with prefix. Escaping "/" as well keeps the whole key in a single path segment, which sidesteps
/// both the path-segment limit and the rule that a blob name may not end in "/" or ".".
/// </remarks>
internal static class BlobCacheKeyEncoder
{
    /// <summary>
    /// Length of the truncation marker plus an unpadded Base64Url SHA-256 hash.
    /// </summary>
    internal const int HashSuffixLength = 44;

    private const string SafeCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    private const char TruncationMarker = '~';

    private static readonly SearchValues<char> Safe = SearchValues.Create(SafeCharacters);

    /// <summary>
    /// Encodes a cache key into a blob name under the application prefix.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="namePrefix">Blob name prefix the key is placed under</param>
    /// <param name="maxEncodedKeyLength">Longest encoded key kept verbatim</param>
    /// <returns>Blob name</returns>
    public static string Encode(string key, string namePrefix, int maxEncodedKeyLength)
    {
        var escaped = Escape(key);

        if (escaped.Length == 0)
        {
            // An empty key would otherwise produce a name ending in "/", which the service rejects.
            // The bare marker is unreachable any other way: a key containing it escapes to "%7E",
            // and a truncated name is always the marker plus a hash.
            return string.Concat(namePrefix, "/", TruncationMarker.ToString());
        }

        if (escaped.Length > maxEncodedKeyLength)
        {
            // The marker is itself escaped when it occurs in a key, so a name carrying one is
            // unambiguously a truncated name rather than a key that happened to contain it.
            var head = TruncateAtEscapeBoundary(escaped, maxEncodedKeyLength - HashSuffixLength);
            var hash = ToBase64Url(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

            escaped = string.Concat(head, TruncationMarker.ToString(), hash);
        }

        return string.Concat(namePrefix, "/", escaped);
    }

    /// <summary>
    /// Encodes the literal part of a key pattern into a blob name prefix for listing.
    /// </summary>
    /// <remarks>
    /// The result filters the listing; it never proves a match. A key long enough to be truncated no
    /// longer carries its own tail in the name, so callers must confirm every listed entry against
    /// the full key recovered from metadata.
    /// </remarks>
    /// <param name="keyPrefix">Literal prefix of the key pattern</param>
    /// <param name="namePrefix">Blob name prefix the key is placed under</param>
    /// <param name="maxEncodedKeyLength">Longest encoded key kept verbatim</param>
    /// <returns>Blob name prefix</returns>
    public static string EncodePrefix(string keyPrefix, string namePrefix, int maxEncodedKeyLength)
    {
        var escaped = Escape(keyPrefix);
        var limit = maxEncodedKeyLength - HashSuffixLength;

        if (escaped.Length > limit)
        {
            escaped = TruncateAtEscapeBoundary(escaped, limit);
        }

        return string.Concat(namePrefix, "/", escaped);
    }

    /// <summary>
    /// Encodes a cache key for entry metadata, where it has to survive as ASCII.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <returns>Base64Url of the key's UTF-8 bytes</returns>
    public static string EncodeKeyForMetadata(string key)
    {
        return ToBase64Url(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>
    /// Recovers a cache key from the value written by <see cref="EncodeKeyForMetadata"/>.
    /// </summary>
    /// <param name="value">Metadata value</param>
    /// <param name="key">Recovered cache key</param>
    /// <returns>Whether the value was well formed</returns>
    public static bool TryDecodeKeyFromMetadata(string? value, out string key)
    {
        key = string.Empty;

        // Absent metadata is a failure; a present but empty value is the empty key, which has to
        // round-trip or an entry stored under it could never be read back.
        if (value is null)
        {
            return false;
        }

        if (value.Length == 0)
        {
            return true;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');

        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => string.Empty,
        };

        if (padded.Length == 0)
        {
            return false;
        }

        var buffer = new byte[(padded.Length / 4) * 3];

        if (!Convert.TryFromBase64String(padded, buffer, out var written))
        {
            return false;
        }

        key = Encoding.UTF8.GetString(buffer, 0, written);

        return true;
    }

    private static string Escape(string value)
    {
        if (!value.AsSpan().ContainsAnyExcept(Safe))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + (value.Length / 2));

        foreach (var character in value)
        {
            if (Safe.Contains(character))
            {
                builder.Append(character);
            }
            else
            {
                AppendEscaped(builder, character);
            }
        }

        return builder.ToString();
    }

    private static void AppendEscaped(StringBuilder builder, char character)
    {
        Span<char> single = stackalloc char[1];
        Span<byte> bytes = stackalloc byte[4];

        single[0] = character;

        // A lone surrogate cannot be transcoded, and a key is free to contain one. Encoding it as
        // the replacement character would collapse distinct keys onto one name, so the code unit is
        // escaped by its own value instead, which stays injective.
        var count = char.IsSurrogate(character)
            ? 0
            : Encoding.UTF8.GetBytes(single, bytes);

        if (count == 0)
        {
            builder
                .Append("%u")
                .Append(((ushort) character).ToString("X4", CultureInfo.InvariantCulture));

            return;
        }

        for (var index = 0; index < count; index++)
        {
            builder.Append('%').Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
        }
    }

    private static string TruncateAtEscapeBoundary(string escaped, int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        if (length >= escaped.Length)
        {
            return escaped;
        }

        // A "%" only ever introduces an escape sequence, since a literal "%" escapes to "%25", so
        // the last few characters are enough to spot a sequence cut in half. The longest sequence
        // is the five-character surrogate form.
        var start = Math.Max(0, length - 5);

        for (var index = length - 1; index >= start; index--)
        {
            if (escaped[index] != '%')
            {
                continue;
            }

            var sequenceLength = escaped[index + 1] == 'u' ? 6 : 3;

            return index + sequenceLength > length
                ? escaped[..index]
                : escaped[..length];
        }

        return escaped[..length];
    }

    private static string ToBase64Url(byte[] bytes)
    {
        // Base64Url arrived in .NET 9 and this package also targets net8.0.
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
