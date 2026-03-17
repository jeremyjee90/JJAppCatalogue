namespace AppCatalogue.Shared.Models;

public sealed class AppActionResult
{
    public bool Success { get; init; }
    public string Status { get; init; } = "Failed";
    public string Message { get; init; } = string.Empty;

    public static AppActionResult Ok(string status, string message) => new()
    {
        Success = true,
        Status = status,
        Message = message
    };

    public static AppActionResult Fail(string message, string status = "Failed") => new()
    {
        Success = false,
        Status = status,
        Message = message
    };
}
