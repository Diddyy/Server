using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Habbo.Web;

/// <summary>How a website (a CMS) signs its calls to the CMS bridge: an HMAC-SHA256, keyed with the shared secret,
/// of the time it was sent, the method, the path with its query, and the body. A call is only taken when the
/// signature matches and it was signed within the allowed clock skew, so an old call can't be replayed later.</summary>
public static class CmsBridgeSignature
{
    public const string TimestampHeader = "X-Cms-Timestamp";
    public const string SignatureHeader = "X-Cms-Signature";

    /// <summary>The text that is signed: one field per line.</summary>
    public static string Payload(long unixSeconds, string method, string pathAndQuery, string body) =>
        string.Join('\n', unixSeconds.ToString(CultureInfo.InvariantCulture), method.ToUpperInvariant(), pathAndQuery, body);

    /// <summary>The signature as lowercase hex.</summary>
    public static string Sign(string secret, long unixSeconds, string method, string pathAndQuery, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(Payload(unixSeconds, method, pathAndQuery, body))));

    /// <summary>Whether a call carries a valid signature, signed no further than <paramref name="maxSkew"/> from
    /// <paramref name="now"/>. An empty secret accepts nothing.</summary>
    public static bool Verify(
        string secret, string? timestampHeader, string? signatureHeader, string method, string pathAndQuery, string body,
        DateTimeOffset now, TimeSpan maxSkew)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader)
            || !long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
            return false;

        if (Math.Abs(now.ToUnixTimeSeconds() - unixSeconds) > maxSkew.TotalSeconds)
            return false;

        var expected = Encoding.ASCII.GetBytes(Sign(secret, unixSeconds, method, pathAndQuery, body));
        var given = Encoding.ASCII.GetBytes(signatureHeader.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }
}
