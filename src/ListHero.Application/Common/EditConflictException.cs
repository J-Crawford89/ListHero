namespace ListHero.Application.Common;

public sealed class EditConflictException : Exception
{
    public EditConflictException() : base("This record changed. Reload it before trying again.") { }
}
