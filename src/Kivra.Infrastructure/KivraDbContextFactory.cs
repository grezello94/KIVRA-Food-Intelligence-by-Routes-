using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Kivra.Infrastructure;
public sealed class KivraDbContextFactory : IDesignTimeDbContextFactory<KivraDbContext> {
 public KivraDbContext CreateDbContext(string[] args) {
  var connectionString=Environment.GetEnvironmentVariable("ConnectionStrings__Default");
  if (string.IsNullOrWhiteSpace(connectionString))
   throw new InvalidOperationException("Set ConnectionStrings__Default before running EF Core commands.");
  var options=new DbContextOptionsBuilder<KivraDbContext>().UseNpgsql(connectionString).Options;
  return new KivraDbContext(options);
 }
}
