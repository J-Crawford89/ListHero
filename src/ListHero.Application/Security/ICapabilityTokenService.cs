namespace ListHero.Application.Security;

public sealed record IssuedCapability(string Token, string Hash)
{
    public override string ToString() => "IssuedCapability { Token = [redacted] }";
}

public interface ICapabilityTokenService
{
    IssuedCapability Issue();
    string HashForLookup(string token);
    bool Matches(string token, string expectedHash);
    string ProtectShareToken(string token);
    string UnprotectShareToken(string protectedToken);
}
