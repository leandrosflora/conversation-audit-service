using conversation_audit_service.Domain;

namespace conversation_audit_service.Application.Ports.Inbound;

public interface IRecordJourneyEventUseCase
{
    Task<RecordJourneyEventResult> ExecuteAsync(JourneyAuditEvent auditEvent, CancellationToken cancellationToken);
}

public enum RecordJourneyEventResult
{
    Recorded,
    RepositoryUnavailable
}
