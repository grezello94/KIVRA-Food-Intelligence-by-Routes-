using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kivra.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
namespace Kivra.Infrastructure;
public interface IPinAuthentication { Task<User?> AuthenticateAsync(string pin, CancellationToken ct); string IssueToken(User user); UserSession? ReadToken(string token); }
public record UserSession(Guid UserId, string DisplayName, UserRole Role, DateTimeOffset ExpiresAt);
public sealed class PinAuthentication(KivraDbContext db, IDataProtectionProvider protection) : IPinAuthentication {
 readonly IDataProtector protector=protection.CreateProtector("Kivra.Session.v1");
 public async Task<User?> AuthenticateAsync(string pin,CancellationToken ct) { if(string.IsNullOrWhiteSpace(pin))return null; foreach(var user in await db.Users.Where(x=>x.Active).ToListAsync(ct)) if(Verify(pin,user.PinHash)) return user; return null; }
 public string IssueToken(User user) => protector.Protect(JsonSerializer.Serialize(new UserSession(user.Id,user.DisplayName,user.Role,DateTimeOffset.UtcNow.AddHours(12))));
 public UserSession? ReadToken(string token) { try { var s=JsonSerializer.Deserialize<UserSession>(protector.Unprotect(token)); return s?.ExpiresAt>DateTimeOffset.UtcNow?s:null; } catch(CryptographicException) { return null; } }
 public static string Hash(string pin) { var salt=RandomNumberGenerator.GetBytes(16); var hash=Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin),salt,210000,HashAlgorithmName.SHA512,32); return $"v1.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}"; }
 static bool Verify(string pin,string stored) { var p=stored.Split('.'); if(p.Length!=3||p[0]!="v1")return false; var actual=Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin),Convert.FromBase64String(p[1]),210000,HashAlgorithmName.SHA512,32); return CryptographicOperations.FixedTimeEquals(actual,Convert.FromBase64String(p[2])); }
}
