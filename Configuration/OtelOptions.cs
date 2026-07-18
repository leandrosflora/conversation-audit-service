namespace conversation_audit_service.Configuration;

public class OtelOptions
{
    public const string SectionName = "Otel";

    public string OtlpEndpoint { get; set; } = string.Empty;
}
