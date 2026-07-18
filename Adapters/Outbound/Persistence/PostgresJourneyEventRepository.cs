using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using conversation_audit_service.Application.Ports.Outbound;
using conversation_audit_service.Domain;
using conversation_audit_service.Platform;

namespace conversation_audit_service.Adapters.Outbound.Persistence;

public class PostgresJourneyEventRepository(
    NpgsqlDataSource dataSource,
    TenantContext tenantContext) : IJourneyEventRepository
{
    private const string ActorType = "system";
    private const string ActorId = "conversation-orchestrator";
    private const string Action = "conversation.journey_processed";
    private const string ResourceType = "conversation";
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    private const string InsertSql = """
        INSERT INTO ops.audit_events
            (tenant_id, actor_type, actor_id, action, resource_type, resource_id, payload, created_at, idempotency_key)
        VALUES
            (@tenant_id, @actor_type, @actor_id, @action, @resource_type, @resource_id, @payload::jsonb, @created_at, @idempotency_key)
        ON CONFLICT (idempotency_key) DO NOTHING;
        """;

    public async Task InsertAsync(
        JourneyAuditEvent auditEvent,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(tenantContext.TenantId, out var tenantId))
            throw new ArgumentException("X-Tenant-Id must be a UUID.");

        var payload = JsonSerializer.Serialize(new { intent = auditEvent.Intent, outcome = auditEvent.Outcome });
        try
        {
            await EnsureSchemaAsync(cancellationToken);
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(InsertSql, connection);
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("actor_type", ActorType);
            command.Parameters.AddWithValue("actor_id", ActorId);
            command.Parameters.AddWithValue("action", Action);
            command.Parameters.AddWithValue("resource_type", ResourceType);
            command.Parameters.AddWithValue("resource_id", auditEvent.ConversationId);
            command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payload });
            command.Parameters.AddWithValue("created_at", auditEvent.Timestamp);
            command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            throw new JourneyEventRepositoryUnavailableException("Failed to reach PostgreSQL.", ex);
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            const string sql = """
                ALTER TABLE ops.audit_events ADD COLUMN IF NOT EXISTS idempotency_key text;
                CREATE UNIQUE INDEX IF NOT EXISTS ux_audit_events_idempotency_key
                    ON ops.audit_events (idempotency_key);
                """;
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaReady = true;
        }
        finally { _schemaLock.Release(); }
    }
}
