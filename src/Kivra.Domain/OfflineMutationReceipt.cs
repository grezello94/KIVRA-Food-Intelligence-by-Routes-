namespace Kivra.Domain;

// Committed with its mutation so a client can safely replay a request when the
// original response never reached the device.
public sealed class OfflineMutationReceipt
{
    public Guid UserId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public int StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? Location { get; set; }
    public required byte[] ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
