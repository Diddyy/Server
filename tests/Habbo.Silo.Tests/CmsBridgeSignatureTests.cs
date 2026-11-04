using Habbo.Web;

namespace Habbo.Silo.Tests;

public class CmsBridgeSignatureTests
{
    private const string Secret = "shared secret";
    private const string Path = "/api/cms/players/0b7e3d4a-1f2c-4c56-9f7b-3a1d2e4f5a6b/reload";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private static bool Verify(string? timestamp, string? signature, string method = "POST", string path = Path, string body = "{}") =>
        CmsBridgeSignature.Verify(Secret, timestamp, signature, method, path, body, Now, Skew);

    [Fact]
    public void ACallSignedWithTheSecretIsTaken()
    {
        var signature = CmsBridgeSignature.Sign(Secret, Now.ToUnixTimeSeconds(), "POST", Path, "{}");

        Assert.True(Verify(Now.ToUnixTimeSeconds().ToString(), signature));
        // the website may send it in capitals
        Assert.True(Verify(Now.ToUnixTimeSeconds().ToString(), signature.ToUpperInvariant()));
    }

    [Fact]
    public void TheSignatureMatchesTheDocumentedPayload()
    {
        // what the CMS computes: hex(hmac_sha256("1800000000\nPOST\n/path\n{}", secret)), as PHP's hash_hmac does
        Assert.Equal("1800000000\nPOST\n" + Path + "\n{}", CmsBridgeSignature.Payload(1_800_000_000, "post", Path, "{}"));
        Assert.Matches("^[0-9a-f]{64}$", CmsBridgeSignature.Sign(Secret, 1, "GET", "/", string.Empty));
    }

    [Theory]
    [InlineData("POST", Path, "{\"amount\":1}")] // another body
    [InlineData("GET", Path, "{}")] // another method
    [InlineData("POST", "/api/cms/hotel/alert", "{}")] // another route
    public void AnythingChangedAfterSigningIsRefused(string method, string path, string body)
    {
        var signature = CmsBridgeSignature.Sign(Secret, Now.ToUnixTimeSeconds(), "POST", Path, "{}");

        Assert.False(Verify(Now.ToUnixTimeSeconds().ToString(), signature, method, path, body));
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void ACallSignedTooLongAgoOrAheadIsRefused(int offsetSeconds)
    {
        var then = Now.ToUnixTimeSeconds() + offsetSeconds;
        var signature = CmsBridgeSignature.Sign(Secret, then, "POST", Path, "{}");

        Assert.False(Verify(then.ToString(), signature));
    }

    [Theory]
    [InlineData(null, "abc")]
    [InlineData("1800000000", null)]
    [InlineData("soon", "abc")]
    [InlineData("-1800000000", "abc")]
    public void MissingOrMalformedHeadersAreRefused(string? timestamp, string? signature) =>
        Assert.False(Verify(timestamp, signature));

    [Fact]
    public void AnotherSecretOrNoSecretIsRefused()
    {
        var unix = Now.ToUnixTimeSeconds();
        var forged = CmsBridgeSignature.Sign("guessed", unix, "POST", Path, "{}");

        Assert.False(Verify(unix.ToString(), forged));
        Assert.False(CmsBridgeSignature.Verify(string.Empty, unix.ToString(), CmsBridgeSignature.Sign(string.Empty, unix, "POST", Path, "{}"), "POST", Path, "{}", Now, Skew));
    }
}
