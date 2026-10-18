using System.Security.Claims;
using System.Text;
using Kivra.Domain;
using Kivra.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kivra.Tests;

public sealed class OfflineMutationMiddlewareTests
{
    [Fact]
    public async Task Replays_persisted_status_location_and_exact_body_without_creating_again()
    {
        await using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        var id = Guid.NewGuid();
        var middleware = new OfflineMutationMiddleware(async context =>
        {
            calls++;
            fixture.Db.Categories.Add(new Category { Id = id, Name = "Offline category" });
            await fixture.Db.SaveChangesAsync();
            context.Response.StatusCode = 201;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.Location = $"/api/categories/{id}";
            await context.Response.WriteAsync($"{{\"id\":\"{id}\",\"name\":\"Offline category\"}}");
        });
        var first = Context(fixture.UserId, "one", "{\"name\":\"Offline category\"}");
        await middleware.InvokeAsync(first, fixture.Db);
        fixture.Db.ChangeTracker.Clear();
        var replay = Context(fixture.UserId, "one", "{\"name\":\"Offline category\"}");
        await middleware.InvokeAsync(replay, fixture.Db);

        Assert.Equal(1, calls);
        Assert.Equal(201, replay.Response.StatusCode);
        Assert.Equal(first.Response.ContentType, replay.Response.ContentType);
        Assert.Equal(first.Response.Headers.Location, replay.Response.Headers.Location);
        Assert.Equal(Body(first), Body(replay));
        Assert.Equal("true", replay.Response.Headers["Idempotent-Replay"].ToString());
        Assert.Single(await fixture.Db.Categories.ToListAsync());
        Assert.Single(await fixture.Db.OfflineMutationReceipts.ToListAsync());
    }

    [Fact]
    public async Task Same_key_with_changed_body_or_route_conflicts_and_keys_are_scoped_to_user()
    {
        await using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        var middleware = new OfflineMutationMiddleware(context =>
        {
            calls++;
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        await middleware.InvokeAsync(Context(fixture.UserId, "key", "one"), fixture.Db);
        var bodyConflict = Context(fixture.UserId, "key", "two");
        await middleware.InvokeAsync(bodyConflict, fixture.Db);
        var routeConflict = Context(fixture.UserId, "key", "one", "/api/storage-locations");
        await middleware.InvokeAsync(routeConflict, fixture.Db);
        var otherUser = Context(Guid.NewGuid(), "key", "two");
        await middleware.InvokeAsync(otherUser, fixture.Db);

        Assert.Equal(409, bodyConflict.Response.StatusCode);
        Assert.Equal(409, routeConflict.Response.StatusCode);
        Assert.Equal(204, otherUser.Response.StatusCode);
        Assert.Equal(2, calls);
        Assert.Equal(2, await fixture.Db.OfflineMutationReceipts.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_mutations_roll_back_prior_saves_and_leave_key_available_for_retry(bool throws)
    {
        await using var fixture = await Fixture.CreateAsync();
        var failing = new OfflineMutationMiddleware(async context =>
        {
            fixture.Db.Categories.Add(new Category { Name = "Must roll back" });
            await fixture.Db.SaveChangesAsync();
            if (throws) throw new InvalidOperationException("Interrupted operation");
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("validation failed");
        });
        if (throws)
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.InvokeAsync(Context(fixture.UserId, "key", "body"), fixture.Db));
        else
            await failing.InvokeAsync(Context(fixture.UserId, "key", "body"), fixture.Db);

        Assert.Empty(await fixture.Db.Categories.ToListAsync());
        Assert.Empty(await fixture.Db.OfflineMutationReceipts.ToListAsync());
        var retry = new OfflineMutationMiddleware(context => { context.Response.StatusCode = 204; return Task.CompletedTask; });
        await retry.InvokeAsync(Context(fixture.UserId, "key", "body"), fixture.Db);
        Assert.Single(await fixture.Db.OfflineMutationReceipts.ToListAsync());
    }

    [Theory]
    [InlineData("POST", "/api/labels/print", false)]
    [InlineData("POST", "/api/labels/11111111-1111-1111-1111-111111111111/reprint", false)]
    [InlineData("POST", "/api/printers/11111111-1111-1111-1111-111111111111/test-print", false)]
    [InlineData("POST", "/api/bridge/pairing-code", false)]
    [InlineData("POST", "/api/auth/pin", false)]
    [InlineData("POST", "/api/users", false)]
    [InlineData("PUT", "/api/users/11111111-1111-1111-1111-111111111111/pin", false)]
    [InlineData("GET", "/api/categories", false)]
    [InlineData("POST", "/api/items", true)]
    [InlineData("POST", "/api/labels/offline-import", true)]
    [InlineData("PATCH", "/api/items/11111111-1111-1111-1111-111111111111/image", true)]
    [InlineData("PATCH", "/api/labels/11111111-1111-1111-1111-111111111111/status", true)]
    public void Replay_allowlist_excludes_authentication_and_every_physical_print_route(string method, string path, bool supported) =>
        Assert.Equal(supported, OfflineMutationMiddleware.Supports(method, path));

    [Fact]
    public async Task Sqlite_upgrade_adds_receipts_to_existing_database_and_is_repeatable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Db.Database.ExecuteSqlRawAsync("DROP TABLE \"OfflineMutationReceipts\"");
        await SchemaCompatibility.EnsureAsync(fixture.Db);
        await SchemaCompatibility.EnsureAsync(fixture.Db);
        var middleware = new OfflineMutationMiddleware(context => { context.Response.StatusCode = 204; return Task.CompletedTask; });
        await middleware.InvokeAsync(Context(fixture.UserId, "upgrade", "body"), fixture.Db);
        Assert.Single(await fixture.Db.OfflineMutationReceipts.ToListAsync());
    }

    static DefaultHttpContext Context(Guid userId, string key, string body, string path = "/api/categories")
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Headers["Idempotency-Key"] = key;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    static byte[] Body(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();

    sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required KivraDbContext Db { get; init; }
        public Guid UserId { get; } = Guid.NewGuid();
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new KivraDbContext(new DbContextOptionsBuilder<KivraDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture { Connection = connection, Db = db };
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}
