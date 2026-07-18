using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using conversation_audit_service.Application.Ports.Outbound;
using conversation_audit_service.Domain;

namespace conversation_audit_service.Adapters.Outbound.Persistence;

public class PostgresJourneyEventRepository(NpgsqlDataSource dataSource) : IJourneyEventRepository
{
    // Fixed mapping from a journey event onto ops.audit_events' generic audit columns (see
    // design.md Decision 3): this service has exactly one caller/event shape today, so the
    // mapping is a constant rather than something the request can influence.
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string ActorType = "system";
    private const string ActorId = "conversation-orchestrator";
    private const string Action = "conversation.journey_processed";
    private const string ResourceType = "conversation";

    private const string InsertSql = """
        INSERT INTO ops.audit_events
            (tenant_id, actor_type, actor_id, action, resource_type, resource_id, payload, created_at)
        VALUES
            (@tenant_id, @actor_type, @actor_id, @action, @resource_type, @resource_id, @payload::jsonb, @created_at)
        """;

    public async Task InsertAsync(JourneyAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { intent = auditEvent.Intent, outcome = auditEvent.Outcome });

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(InsertSql, connection);

            command.Parameters.AddWithValue("tenant_id", TenantId);
            command.Parameters.AddWithValue("actor_type", ActorType);
            command.Parameters.AddWithValue("actor_id", ActorId);
            command.Parameters.AddWithValue("action", Action);
            command.Parameters.AddWithValue("resource_type", ResourceType);
            command.Parameters.AddWithValue("resource_id", auditEvent.ConversationId);
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });
            command.Parameters.AddWithValue("created_at", auditEvent.Timestamp);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new JourneyEventRepositoryUnavailableException("Failed to reach PostgreSQL.", ex);
        }
    }
}
