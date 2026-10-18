namespace Kivra.Application;

public sealed class IdempotencyConflictException(string message) : InvalidOperationException(message);
