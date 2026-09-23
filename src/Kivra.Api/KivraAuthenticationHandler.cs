using System.Security.Claims;
using System.Text.Encodings.Web;
using Kivra.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
namespace Kivra.Api;
public sealed class KivraAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IPinAuthentication sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder) {
 protected override Task<AuthenticateResult> HandleAuthenticateAsync() { var token=Request.Headers.Authorization.FirstOrDefault()?.Split(' ',2).Last(); if(string.IsNullOrWhiteSpace(token))return Task.FromResult(AuthenticateResult.NoResult()); var session=sessions.ReadToken(token); if(session is null)return Task.FromResult(AuthenticateResult.Fail("Invalid or expired session.")); var claims=new[]{new Claim(ClaimTypes.NameIdentifier,session.UserId.ToString()),new Claim(ClaimTypes.Name,session.DisplayName),new Claim(ClaimTypes.Role,session.Role.ToString())}; return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name)),Scheme.Name))); }
}
