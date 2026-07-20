using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace conversation_audit_service.Tests.Testing;

/// <summary>
/// Mirrors conversation-audit-service's own Platform/PlatformServices.cs (no InternalAuth
/// Enabled toggle - FallbackPolicy always requires an authenticated user) so
/// WebApplicationFactory-based endpoint tests can mint a JWT that satisfies it, instead of
/// bypassing auth entirely.
/// </summary>
public static class TestAuth
{
    public const string InboundCaller = "conversation-orchestrator";
    public const string SigningKey = "test-only-internal-auth-inbound-secret-32-bytes-min";
    public const string Issuer = "conversational-ai-platform";
    public const string Audience = "conversation-audit-service";
    public const string TenantId = "00000000-0000-0000-0000-000000000001";

    public static void ConfigureSigningKey(IWebHostBuilder builder) =>
        builder.UseSetting($"InternalAuth:InboundSecrets:{InboundCaller}", SigningKey);

    public static string IssueToken()
    {
        var now = DateTime.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, InboundCaller),
            new Claim("tenant_id", TenantId)
        };
        var payload = new JwtPayload(Issuer, Audience, claims, now, now.AddMinutes(5));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var header = new JwtHeader(credentials) { ["kid"] = InboundCaller };
        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
