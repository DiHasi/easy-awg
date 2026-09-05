namespace AwgEasy.Contracts;

public sealed record ApiError(string Code, string Message)
{
    public static ApiError Empty { get; } = new(string.Empty, string.Empty);
}
