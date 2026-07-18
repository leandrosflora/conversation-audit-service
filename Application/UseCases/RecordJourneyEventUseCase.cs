using conversation_audit_service.Application.Ports.Inbound;
using conversation_audit_service.Application.Ports.Outbound;
using conversation_audit_service.Domain;

namespace conversation_audit_service.Application.UseCases;

public class RecordJourneyEventUseCase(
    IJourneyEventRepository repository,
    ILogger<RecordJourneyEventUseCase> logger) : IRecordJourneyEventUseCase
{
    public async Task<RecordJourneyEventResult> ExecuteAsync(
        JourneyAuditEvent auditEvent,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await repository.InsertAsync(auditEvent, idempotencyKey, cancellationToken);
            return RecordJourneyEventResult.Recorded;
        }
        catch (JourneyEventRepositoryUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Failed to persist journey audit event for conversation {ConversationId}: repository unavailable",
                auditEvent.ConversationId);
            return RecordJourneyEventResult.RepositoryUnavailable;
        }
    }
}
