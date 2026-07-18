using conversation_audit_service.Application.Ports.Inbound;
using conversation_audit_service.Domain;

namespace conversation_audit_service.Adapters.Inbound.Http;

public static class JourneyEventEndpoints
{
    public static IEndpointRouteBuilder MapJourneyEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/journey-events", HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        JourneyEventRequest request,
        HttpRequest httpRequest,
        IRecordJourneyEventUseCase useCase,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = httpRequest.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.BadRequest(new { error = "Idempotency-Key header is required." });
        }

        if (string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return Results.BadRequest(new { error = "conversationId is required." });
        }

        if (string.IsNullOrWhiteSpace(request.Outcome))
        {
            return Results.BadRequest(new { error = "outcome is required." });
        }

        if (request.Timestamp is null)
        {
            return Results.BadRequest(new { error = "timestamp is required." });
        }

        var auditEvent = new JourneyAuditEvent
        {
            ConversationId = request.ConversationId,
            Intent = request.Intent,
            Outcome = request.Outcome,
            Timestamp = request.Timestamp.Value
        };

        var result = await useCase.ExecuteAsync(auditEvent, idempotencyKey, cancellationToken);

        return result switch
        {
            RecordJourneyEventResult.Recorded => Results.Accepted(),
            RecordJourneyEventResult.RepositoryUnavailable => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    }
}

public record JourneyEventRequest(string? ConversationId, string? Intent, string? Outcome, DateTimeOffset? Timestamp);
