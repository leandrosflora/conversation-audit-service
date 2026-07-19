using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using conversation_audit_service.Application.Ports.Inbound;
using conversation_audit_service.Domain;
using conversation_audit_service.Tests.Testing;

namespace conversation_audit_service.Tests.Endpoints;

public class JourneyEventEndpointsTests
{
    [Fact]
    public async Task PostJourneyEvents_ValidRequest_ReturnsAccepted()
    {
        var client = BuildClient(new StubUseCase(RecordJourneyEventResult.Recorded));

        var response = await client.PostAsJsonAsync("/journey-events", new
        {
            conversationId = "5511999990000",
            intent = "debt_renegotiation",
            outcome = "processed",
            timestamp = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task PostJourneyEvents_IntentOmitted_ReturnsAccepted()
    {
        var client = BuildClient(new StubUseCase(RecordJourneyEventResult.Recorded));

        var response = await client.PostAsJsonAsync("/journey-events", new
        {
            conversationId = "5511999990000",
            outcome = "handoff",
            timestamp = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task PostJourneyEvents_MissingConversationId_ReturnsBadRequest()
    {
        var client = BuildClient(new StubUseCase(RecordJourneyEventResult.Recorded));

        var response = await client.PostAsJsonAsync("/journey-events", new
        {
            outcome = "processed",
            timestamp = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostJourneyEvents_MissingOutcome_ReturnsBadRequest()
    {
        var client = BuildClient(new StubUseCase(RecordJourneyEventResult.Recorded));

        var response = await client.PostAsJsonAsync("/journey-events", new
        {
            conversationId = "5511999990000",
            timestamp = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostJourneyEvents_RepositoryUnavailable_ReturnsServiceUnavailable()
    {
        var client = BuildClient(new StubUseCase(RecordJourneyEventResult.RepositoryUnavailable));

        var response = await client.PostAsJsonAsync("/journey-events", new
        {
            conversationId = "5511999990000",
            outcome = "processed",
            timestamp = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private static HttpClient BuildClient(IRecordJourneyEventUseCase useCase)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            TestAuth.ConfigureSigningKey(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRecordJourneyEventUseCase>();
                services.AddScoped(_ => useCase);
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.IssueToken());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TestAuth.TenantId);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idem-test-1");
        return client;
    }

    private class StubUseCase(RecordJourneyEventResult result) : IRecordJourneyEventUseCase
    {
        public Task<RecordJourneyEventResult> ExecuteAsync(
            JourneyAuditEvent auditEvent, string idempotencyKey, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }
}
