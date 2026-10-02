namespace ListHero.Application.Common;

public sealed class ResourceNotFoundException() : Exception("The requested list was not found.");
public sealed class AccountUnavailableException() : Exception("This account is unavailable.");
