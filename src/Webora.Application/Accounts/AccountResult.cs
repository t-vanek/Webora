namespace Webora.Application.Accounts;

public sealed record AccountResult
{
    public bool Succeeded { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static readonly AccountResult Success = new() { Succeeded = true };

    public static AccountResult Failure(params string[] errors) => new() { Succeeded = false, Errors = errors };
}
