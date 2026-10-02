using System.Security.Cryptography;
using System.Text;
using ListHero.Application.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace ListHero.Infrastructure.Security;

public sealed class CapabilityTokenService(IDataProtectionProvider protection) : ICapabilityTokenService
{
    private readonly IDataProtector _shareProtector = protection.CreateProtector("ListHero.ShareLinks.v1");

    public IssuedCapability Issue()
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return new(token, Convert.ToHexString(Hash(token)));
    }

    public bool Matches(string token, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128 || expectedHash.Length != 64) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Hash(token), Convert.FromHexString(expectedHash));
        }
        catch (FormatException) { return false; }
    }

    public string HashForLookup(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) throw new ArgumentException("Invalid capability.");
        return Convert.ToHexString(Hash(token));
    }

    public string ProtectShareToken(string token) => _shareProtector.Protect(token);
    public string UnprotectShareToken(string protectedToken) => _shareProtector.Unprotect(protectedToken);
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
