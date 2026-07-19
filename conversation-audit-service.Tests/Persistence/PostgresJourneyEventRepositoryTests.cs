using System.Text.Json;
using Npgsql;
using conversation_audit_service.Adapters.Outbound.Persistence;
using conversation_audit_service.Application.Ports.Outbound;
using conversation_audit_service.Domain;
using conversation_audit_service.Platform;

namespace conversation_audit_service.Tests.Persistence;

public class PostgresJourneyEventRepositoryTests(PostgresJourneyEventRepositoryFixture fixture)
    : IClassFixture<PostgresJourneyEventRepositoryFixture>
{
    private const string TenantId = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task InsertAsync_ValidEvent_WritesExpectedRow()
    {
        await using var dataSource = fixture.CreateDataSource();
        var tenantContext = new TenantContext();
        using var tenantScope = tenantContext.Push(TenantId);
        var repository = new PostgresJourneyEventRepository(dataSource, tenantContext);
        var timestamp = DateTimeOffset.Parse("2026-07-17T12:00:00Z");

        await repository.InsertAsync(
            new JourneyAuditEvent
            {
                ConversationId = "5511999990000",
                Intent = "debt_renegotiation",
                Outcome = "processed",
                Timestamp = timestamp
            },
            "idem-audit-1",
            CancellationToken.None);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT tenant_id, actor_type, actor_id, action, resource_type, resource_id, payload, created_at
            FROM ops.audit_events
            WHERE resource_id = @resource_id
            """,
            connection);
        command.Parameters.AddWithValue("resource_id", "5511999990000");

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), reader.GetGuid(0));
        Assert.Equal("system", reader.GetString(1));
        Assert.Equal("conversation-orchestrator", reader.GetString(2));
        Assert.Equal("conversation.journey_processed", reader.GetString(3));
        Assert.Equal("conversation", reader.GetString(4));
        Assert.Equal("5511999990000", reader.GetString(5));

        using var payload = JsonDocument.Parse(reader.GetString(6));
        Assert.Equal("debt_renegotiation", payload.RootElement.GetProperty("intent").GetString());
        Assert.Equal("processed", payload.RootElement.GetProperty("outcome").GetString());

        Assert.Equal(timestamp, reader.GetFieldValue<DateTimeOffset>(7));

        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task InsertAsync_NullIntent_PersistsNullInPayload()
    {
        await using var dataSource = fixture.CreateDataSource();
        var tenantContext = new TenantContext();
        using var tenantScope = tenantContext.Push(TenantId);
        var repository = new PostgresJourneyEventRepository(dataSource, tenantContext);

        await repository.InsertAsync(
            new JourneyAuditEvent
            {
                ConversationId = "5511999990001",
                Intent = null,
                Outcome = "handoff",
                Timestamp = DateTimeOffset.UtcNow
            },
            "idem-audit-2",
            CancellationToken.None);

        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT payload FROM ops.audit_events WHERE resource_id = @resource_id", connection);
        command.Parameters.AddWithValue("resource_id", "5511999990001");

        var payload = (string)(await command.ExecuteScalarAsync())!;
        using var payloadJson = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Null, payloadJson.RootElement.GetProperty("intent").ValueKind);
    }

    [Fact]
    public async Task InsertAsync_RepositoryUnreachable_ThrowsRepositoryUnavailableException()
    {
        // A data source pointed at a port nothing listens on, with a short timeout, simulates
        // PostgreSQL being unreachable without needing to actually stop the shared container.
        var connectionStringBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Port = 1,
            Timeout = 1
        };
        await using var unreachableDataSource = NpgsqlDataSource.Create(connectionStringBuilder.ConnectionString);
        var tenantContext = new TenantContext();
        using var tenantScope = tenantContext.Push(TenantId);
        var repository = new PostgresJourneyEventRepository(unreachableDataSource, tenantContext);

        await Assert.ThrowsAsync<JourneyEventRepositoryUnavailableException>(() => repository.InsertAsync(
            new JourneyAuditEvent
            {
                ConversationId = "5511999990002",
                Outcome = "processed",
                Timestamp = DateTimeOffset.UtcNow
            },
            "idem-audit-3",
            CancellationToken.None));
    }
}
