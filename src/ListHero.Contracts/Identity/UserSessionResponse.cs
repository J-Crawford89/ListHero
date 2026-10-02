namespace ListHero.Contracts.Identity;

public sealed record UserSessionResponse(string Subject, string? DisplayName, bool IsAdmin);

public static class AppRoles
{
    public const string User = "User";
    public const string Admin = "Admin";
}
