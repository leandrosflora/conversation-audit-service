using conversation_audit_service.Application.Ports.Inbound;
using conversation_audit_service.Domain;
using conversation_audit_service.Platform;

namespace conversation_audit_service.Adapters.Inbound.Http;

public static class JourneyEventEndpoints
{
    public static IEndpointRouteBuilder MapJourneyEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/journey-events", HandleAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        JourneyEventRequest request,
        HttpContext httpContext,
        IRecordJourneyEventUseCase useCase,
        TenantContext tenantContext,
        PlatformMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (!TenantContext.TryResolve(
                httpContext.User,
                httpContext.Request.Headers["X-Tenant-Id"].ToString(),
                out var tenantId))
        {
            return Results.Json(
                new { error = "X-Tenant-Id must be a UUID and match the signed tenant_id claim." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Results.BadRequest(new { error = "Idempotency-Key header is required." });
        if (string.IsNullOrWhiteSpace(request.ConversationId))
            return Results.BadRequest(new { error = "conversationId is required." });
        if (string.IsNullOrWhiteSpace(request.Outcome))
            return Results.BadRequest(new { error = "outcome is required." });
        if (request.Timestamp is null)
            return Results.BadRequest(new { error = "timestamp is required." });

        using var tenantScope = tenantContext.Push(tenantId);
        var result = await useCase.ExecuteAsync(
            new JourneyAuditEvent
            {
                ConversationId = request.ConversationId,
                Intent = request.Intent,
                Outcome = request.Outcome,
                Timestamp = request.Timestamp.Value
            },
            idempotencyKey,
            cancellationToken);

        metrics.Increment("audit_events_total", ("result", result.ToString().ToLowerInvariant()));
        return result switch
        {
            RecordJourneyEventResult.Recorded => Results.Accepted(),
            RecordJourneyEventResult.RepositoryUnavailable => Results.StatusCode(503),
            _ => Results.StatusCode(500)
        };
    }
}

public record JourneyEventRequest(string? ConversationId, string? Intent, string? Outcome, DateTimeOffset? Timestamp);
