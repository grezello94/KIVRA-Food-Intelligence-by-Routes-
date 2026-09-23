using Kivra.Domain;
using Microsoft.EntityFrameworkCore;
namespace Kivra.Infrastructure;
public sealed class KivraDbContext(DbContextOptions<KivraDbContext> options) : DbContext(options) {
 public DbSet<Item> Items => Set<Item>(); public DbSet<Category> Categories => Set<Category>(); public DbSet<StorageLocation> StorageLocations => Set<StorageLocation>(); public DbSet<Printer> Printers => Set<Printer>(); public DbSet<Label> Labels => Set<Label>(); public DbSet<PrintJob> PrintJobs => Set<PrintJob>(); public DbSet<User> Users => Set<User>(); public DbSet<AuditLog> AuditLogs => Set<AuditLog>(); public DbSet<Notification> Notifications => Set<Notification>();
 protected override void OnModelCreating(ModelBuilder b) { base.OnModelCreating(b);
  b.Entity<Item>().HasIndex(x => x.Name).IsUnique(); b.Entity<Label>().HasIndex(x => x.LabelCode).IsUnique(); b.Entity<Label>().HasIndex(x => x.ExpiryDateTime); b.Entity<Label>().HasIndex(x => x.CurrentStatus); b.Entity<Label>().HasIndex(x => x.ItemId); b.Entity<Label>().HasIndex(x => x.CreatedAt); b.Entity<PrintJob>().HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
  b.Entity<Item>().Property(x => x.ImageDataUrl).HasColumnType("TEXT"); b.Entity<Label>().Property(x => x.ShelfLifeRuleSnapshot).HasMaxLength(100); b.Entity<PrintJob>().Property(x => x.Payload).HasColumnType("TEXT"); b.Entity<AuditLog>().Property(x => x.NewValues).HasColumnType("TEXT");
 }
}
