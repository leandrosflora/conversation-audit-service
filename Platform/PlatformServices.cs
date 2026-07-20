using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace conversation_audit_service.Platform;

public sealed class InternalAuthOptions
{
    public const string SectionName = "InternalAuth";
    public string Issuer { get; init; } = "conversational-ai-platform";
    public string ServiceName { get; init; } = "conversation-audit-service";

    /// <summary>Secrets this service uses to sign outbound tokens, keyed by audience. Empty here -
    /// conversation-audit-service never calls another service's internal-auth-protected endpoint.</summary>
    public Dictionary<string, string> OutboundSecrets { get; init; } = new();

    /// <summary>Secrets this service uses to validate inbound tokens, keyed by the calling
    /// service's own name (which also must equal that token's `kid` header and `sub` claim).</summary>
    public Dictionary<string, string> InboundSecrets { get; init; } = new();

    public static bool HasValidSecret(string? secret) =>
        !string.IsNullOrEmpty(secret) && Encoding.UTF8.GetByteCount(secret) >= 32;
}

public sealed class TenantContext
{
    public const string ClaimType = "tenant_id";
    private static readonly AsyncLocal<string?> Current = new();
    public string TenantId => Current.Value ?? throw new InvalidOperationException("Tenant context unavailable.");

    public IDisposable Push(string tenantId)
    {
        if (!TryNormalize(tenantId, out var canonical))
        {
            throw new ArgumentException("Tenant ID must be a non-empty UUID.", nameof(tenantId));
        }
        var previous = Current.Value;
        Current.Value = canonical;
        return new Scope(() => Current.Value = previous);
    }

    public static bool TryNormalize(string? tenantId, out string canonical)
    {
        canonical = string.Empty;
        if (!Guid.TryParse(tenantId?.Trim(), out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }
        canonical = parsed.ToString("D");
        return true;
    }

    public static bool TryResolve(ClaimsPrincipal principal, string? headerTenant, out string tenantId)
    {
        tenantId = string.Empty;
        if (!TryNormalize(headerTenant, out var header)
            || !TryNormalize(principal.FindFirstValue(ClaimType), out var claim)
            || !string.Equals(header, claim, StringComparison.Ordinal))
        {
            return false;
        }
        tenantId = claim;
        return true;
    }

    private sealed class Scope(Action release) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            release();
        }
    }
}

/// <summary>Signs outbound internal-auth JWTs. conversation-audit-service currently has no
/// outbound calls of its own (OutboundSecrets stays empty) - kept for structural parity with
/// the other services sharing this platform code shape, and in case an outbound call is ever
/// added here.</summary>
public sealed class InternalTokenService(IOptions<InternalAuthOptions> options)
{
    public string CreateToken(string audience, string tenantId)
    {
        var value = options.Value;
        if (!value.OutboundSecrets.TryGetValue(audience, out var secret) || !InternalAuthOptions.HasValidSecret(secret))
        {
            throw new InvalidOperationException(
                $"InternalAuth:OutboundSecrets:{audience} must be configured with at least 32 UTF-8 bytes.");
        }
        if (!TenantContext.TryNormalize(tenantId, out var canonicalTenant))
        {
            throw new ArgumentException("Tenant ID must be a non-empty UUID.", nameof(tenantId));
        }

        var now = DateTime.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, value.ServiceName),
            new Claim(TenantContext.ClaimType, canonicalTenant),
            new Claim(
                JwtRegisteredClaimNames.Iat,
                EpochTime.GetIntDate(now).ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("n"))
        };

        var payload = new JwtPayload(value.Issuer, audience, claims, now, now.AddMinutes(5));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);
        var header = new JwtHeader(credentials) { ["kid"] = value.ServiceName };
        var token = new JwtSecurityToken(header, payload);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class PlatformMetrics
{
    private readonly ConcurrentDictionary<string, long> _values = new();
    public void Increment(string name, params (string Name, string Value)[] labels) =>
        _values.AddOrUpdate(Key(name, labels), 1, (_, value) => value + 1);
    public string Render() => string.Join('\n', _values.OrderBy(item => item.Key).Select(item => $"{item.Key} {item.Value}")) + "\n";

    private static string Key(string name, params (string Name, string Value)[] labels)
    {
        if (labels.Length == 0) return name;
        var rendered = string.Join(",", labels.Select(label =>
            $"{Regex.Replace(label.Name, "[^a-zA-Z0-9_:]", "_")}=\"{label.Value.Replace("\"", "\\\"")}\""));
        return $"{name}{{{rendered}}}";
    }
}

public static class PlatformExtensions
{
    public static IServiceCollection AddPlatform(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection(InternalAuthOptions.SectionName).Get<InternalAuthOptions>() ?? new();
        services.Configure<InternalAuthOptions>(configuration.GetSection(InternalAuthOptions.SectionName));
        services.AddSingleton<TenantContext>();
        services.AddSingleton<InternalTokenService>();
        services.AddSingleton<PlatformMetrics>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = auth.Issuer,
                ValidateAudience = true,
                ValidAudience = auth.ServiceName,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeyResolver = (_, _, kid, _) =>
                {
                    if (kid is null
                        || !auth.InboundSecrets.TryGetValue(kid, out var secret)
                        || !InternalAuthOptions.HasValidSecret(secret))
                    {
                        return Array.Empty<SecurityKey>();
                    }
                    return new SecurityKey[] { new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)) };
                },
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = JwtRegisteredClaimNames.Sub
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var kid = context.SecurityToken switch
                    {
                        JwtSecurityToken jwtSecurityToken => jwtSecurityToken.Header.Kid,
                        Microsoft.IdentityModel.JsonWebTokens.JsonWebToken jsonWebToken => jsonWebToken.Kid,
                        _ => null
                    };
                    var sub = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
                    if (kid is null || !string.Equals(kid, sub, StringComparison.Ordinal))
                    {
                        context.HttpContext.RequestServices
                            .GetRequiredService<PlatformMetrics>()
                            .Increment("platform_internal_auth_failures_total", ("reason", "kid_sub_mismatch"));
                        context.Fail("Token kid header does not match sub claim.");
                    }
                    return Task.CompletedTask;
                }
            };
        });
        services.AddAuthorization(options =>
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireClaim(JwtRegisteredClaimNames.Sub)
                .RequireClaim(TenantContext.ClaimType)
                .Build());
        return services;
    }

    public static WebApplication UsePlatform(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        app.MapGet("/metrics", (PlatformMetrics metrics) => Results.Text(metrics.Render(), "text/plain; version=0.0.4")).AllowAnonymous();
        return app;
    }
}
