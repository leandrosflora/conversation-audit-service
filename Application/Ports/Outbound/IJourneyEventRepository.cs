using conversation_audit_service.Domain;

namespace conversation_audit_service.Application.Ports.Outbound;

public interface IJourneyEventRepository
{
    /// <summary>Throws <see cref="JourneyEventRepositoryUnavailableException"/> if PostgreSQL cannot be reached.</summary>
    Task InsertAsync(JourneyAuditEvent auditEvent, CancellationToken cancellationToken);
}

public class JourneyEventRepositoryUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
