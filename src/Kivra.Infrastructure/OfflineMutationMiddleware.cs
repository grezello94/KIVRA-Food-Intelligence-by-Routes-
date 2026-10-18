using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Kivra.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Kivra.Infrastructure;

/// <summary>Durable replay protection for database-only offline mutations.</summary>
public sealed class OfflineMutationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, KivraDbContext db)
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (!Supports(context.Request.Method, context.Request.Path) || string.IsNullOrEmpty(key) ||
            !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            await next(context);
            return;
        }
        if (key.Length > 128 || string.IsNullOrWhiteSpace(key) || key != key.Trim())
        {
            await Results.BadRequest(new { error = "Idempotency-Key must contain 1 to 128 characters without surrounding whitespace." }).ExecuteAsync(context);
            return;
        }

        context.Request.EnableBuffering();
        var bodyHash = Convert.ToHexString(await SHA256.HashDataAsync(context.Request.Body, context.RequestAborted));
        context.Request.Body.Position = 0;
        var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{context.Request.Method}\n{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}\n{context.Request.ContentType}\n{bodyHash}")));
        var originalBody = context.Response.Body;

        // The execution strategy encloses the transaction, including its receipt.
        // A lost commit acknowledgement retries by reading the persisted receipt.
        var response = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
                if (db.Database.IsNpgsql())
                {
                    // Serialize this user's key across API instances. Native
                    // printer and bridge I/O must stay outside this transaction.
                    var lockBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:D}\n{key}"));
                    var lockId = BinaryPrimitives.ReadInt64BigEndian(lockBytes);
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockId})", context.RequestAborted);
                }
                var prior = await db.OfflineMutationReceipts.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.UserId == userId && x.IdempotencyKey == key, context.RequestAborted);
                if (prior is not null)
                {
                    if (prior.RequestHash != requestHash)
                        return new CapturedResponse(409, "application/json", null,
                            "{\"error\":\"This Idempotency-Key was already used for a different request.\"}"u8.ToArray(), false);
                    return new CapturedResponse(prior.StatusCode, prior.ContentType, prior.Location, prior.ResponseBody, true);
                }

                using var buffer = new MemoryStream();
                context.Request.Body.Position = 0;
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = null;
                context.Response.Headers.Remove("Location");
                context.Response.Headers.Remove("Content-Length");
                context.Response.Body = buffer;
                try { await next(context); }
                finally { context.Response.Body = originalBody; }
                var captured = new CapturedResponse(context.Response.StatusCode, context.Response.ContentType,
                    context.Response.Headers.Location.FirstOrDefault(), buffer.ToArray(), false);
                if (captured.StatusCode is >= 200 and < 300)
                {
                    db.OfflineMutationReceipts.Add(new OfflineMutationReceipt
                    {
                        UserId = userId, IdempotencyKey = key, RequestHash = requestHash,
                        StatusCode = captured.StatusCode, ContentType = captured.ContentType,
                        Location = captured.Location, ResponseBody = captured.Body
                    });
                    await db.SaveChangesAsync(context.RequestAborted);
                    await transaction.CommitAsync(context.RequestAborted);
                }
                else
                {
                    await transaction.RollbackAsync(context.RequestAborted);
                    db.ChangeTracker.Clear();
                }
                return captured;
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
            finally { context.Response.Body = originalBody; }
        });

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        context.Response.Headers.Remove("Content-Length");
        if (response.Location is not null) context.Response.Headers.Location = response.Location;
        else context.Response.Headers.Remove("Location");
        if (response.Replayed) context.Response.Headers["Idempotent-Replay"] = "true";
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }

    public static bool Supports(string method, PathString path)
    {
        var segments = (path.Value ?? "").Trim('/').Split('/');
        if (segments.Length < 2 || segments[0] != "api") return false;
        if (segments.Length == 2)
            return method == "POST" && segments[1] is "items" or "categories" or "storage-locations" or "printers";
        if (segments.Length == 3 && method == "POST" && segments[1] == "labels" && segments[2] == "offline-import") return true;
        if (!Guid.TryParse(segments[2], out _)) return false;
        if (segments.Length == 3) return method == "PUT" && segments[1] is "items" or "printers";
        if (segments.Length != 4) return false;
        return (method, segments[1], segments[3]) switch
        {
            ("PATCH", "items", "image") => true,
            ("PATCH", "storage-locations", "active") => true,
            ("PATCH", "labels", "status") => true,
            ("POST", "notifications", "read") => true,
            _ => false
        };
    }

    sealed record CapturedResponse(int StatusCode, string? ContentType, string? Location, byte[] Body, bool Replayed);
}
