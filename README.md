# Serviço de Auditoria de Conversas

Serviço em ASP.NET Core responsável por registrar eventos de auditoria gerados durante o processamento de jornadas conversacionais.

A API recebe informações sobre a conversa, intenção, resultado e data do processamento, valida a solicitação e persiste o evento na tabela `ops.audit_events` do PostgreSQL. O serviço também exporta rastreamentos distribuídos por meio do OpenTelemetry.

## O que este serviço faz

- Recebe eventos de jornada pelo endpoint `POST /journey-events`, autenticado com JWT interno.
- Resolve o tenant a partir da claim assinada `tenant_id` (validada contra o header `X-Tenant-Id`), não mais de um valor fixo.
- Exige um header `Idempotency-Key`, persistido com constraint única por `(tenant_id, idempotency_key)` — reenvio da mesma chave não duplica o evento.
- Valida o identificador da conversa, o resultado e o timestamp do evento.
- Registra o evento na tabela `ops.audit_events`.
- Armazena `intent` e `outcome` em uma coluna JSONB.
- Retorna `503 Service Unavailable` quando o PostgreSQL não está acessível.
- Expõe `GET /health/ready`.
- Exporta traces do ASP.NET Core e do Npgsql por OTLP.
- Adiciona `TraceId`, `SpanId` e `ParentId` aos logs da aplicação.

## Arquitetura

O código utiliza portas e adaptadores, seguindo os princípios da arquitetura hexagonal:

```mermaid
flowchart LR
    Client[Orquestrador ou serviço cliente] -->|POST /journey-events| HTTP[Adaptador HTTP de entrada]
    HTTP --> UC[Caso de uso de registro de evento]
    UC --> PORT[Porta do repositório de auditoria]
    PORT --> PG[Adaptador PostgreSQL de saída]
    PG --> DB[(ops.audit_events)]

    HTTP -. traces .-> OTEL[Coletor OpenTelemetry]
    PG -. traces .-> OTEL
```

### Fluxo da solicitação

```mermaid
sequenceDiagram
    participant Client as Serviço cliente
    participant API as API de auditoria
    participant UseCase as RecordJourneyEventUseCase
    participant PostgreSQL

    Client->>API: POST /journey-events
    API->>API: Valida conversationId, outcome e timestamp
    API->>UseCase: Executa o registro do evento
    UseCase->>PostgreSQL: INSERT em ops.audit_events

    alt Evento persistido
        PostgreSQL-->>UseCase: Sucesso
        UseCase-->>API: Recorded
        API-->>Client: 202 Accepted
    else PostgreSQL indisponível
        PostgreSQL--xUseCase: Erro de conexão ou timeout
        UseCase-->>API: RepositoryUnavailable
        API-->>Client: 503 Service Unavailable
    end
```

## Tecnologias

| Área | Tecnologia |
|---|---|
| Runtime | .NET 8 |
| API | ASP.NET Core Minimal APIs |
| Documentação da API | Swagger / OpenAPI |
| Banco de dados | PostgreSQL |
| Acesso a dados | Npgsql |
| Observabilidade | OpenTelemetry + OTLP |
| Containerização | Docker com build multi-stage |

## Referência da API

### Registrar um evento de jornada

```http
POST /journey-events
Content-Type: application/json
Authorization: Bearer <jwt-interno>
X-Tenant-Id: <tenant>
Idempotency-Key: <chave-estável-por-evento>
```

#### Corpo da solicitação

```json
{
  "conversationId": "conversation-12345",
  "intent": "renegociar_divida",
  "outcome": "journey_completed",
  "timestamp": "2026-07-18T15:30:00Z"
}
```

| Campo | Tipo | Obrigatório | Descrição |
|---|---|---:|---|
| `conversationId` | string | Sim | Identificador da conversa auditada. |
| `intent` | string ou `null` | Não | Intenção identificada durante a conversa. |
| `outcome` | string | Sim | Resultado do processamento da jornada. |
| `timestamp` | string ISO 8601 | Sim | Data e hora em que o evento ocorreu. |

#### Respostas

| Status | Significado |
|---:|---|
| `202 Accepted` | O evento foi persistido com sucesso (ou já havia sido, para a mesma `Idempotency-Key`). |
| `400 Bad Request` | `conversationId`, `outcome`, `timestamp` ou `Idempotency-Key` não foi informado corretamente. |
| `401 Unauthorized` | JWT ausente, inválido ou expirado. |
| `403 Forbidden` | `X-Tenant-Id` não é UUID ou não bate com a claim `tenant_id` assinada. |
| `503 Service Unavailable` | O PostgreSQL está indisponível ou excedeu o timeout. |
| `500 Internal Server Error` | Ocorreu uma falha inesperada na aplicação. |

#### Exemplo com cURL

```bash
curl --request POST \
  --url http://localhost:5021/journey-events \
  --header 'Content-Type: application/json' \
  --header 'Authorization: Bearer <jwt-interno>' \
  --header 'X-Tenant-Id: 00000000-0000-0000-0000-000000000001' \
  --header 'Idempotency-Key: conversation-12345:journey_completed' \
  --data '{
    "conversationId": "conversation-12345",
    "intent": "renegociar_divida",
    "outcome": "journey_completed",
    "timestamp": "2026-07-18T15:30:00Z"
  }'
```

## Comportamento da persistência

A implementação atual converte o evento recebido para o modelo genérico da tabela `ops.audit_events`.

| Coluna | Valor persistido |
|---|---|
| `tenant_id` | Resolvido da claim `tenant_id` assinada no JWT (validada contra `X-Tenant-Id`) |
| `actor_type` | `system` (fixo) |
| `actor_id` | `conversation-orchestrator` (fixo) |
| `action` | `conversation.journey_processed` (fixo) |
| `resource_type` | `conversation` (fixo) |
| `resource_id` | Valor recebido em `conversationId` |
| `payload` | JSON contendo `intent` e `outcome` |
| `created_at` | Valor recebido em `timestamp` |
| `idempotency_key` | Valor recebido no header, com constraint única por `(tenant_id, idempotency_key)` |

Exemplo do conteúdo persistido em `payload`:

```json
{
  "intent": "renegociar_divida",
  "outcome": "journey_completed"
}
```

> [!IMPORTANT]
> `actor_type`, `actor_id`, `action` e `resource_type` continuam fixos no código — apenas `tenant_id` passou a ser dinâmico (derivado do token). O cliente ainda não pode alterar `actor`/`action` pela API.

O banco de dados deve possuir previamente:

- O schema `ops`.
- A tabela `ops.audit_events`.
- Colunas compatíveis com o comando de inserção documentado acima.

Este repositório não contém migrações de banco. Os comentários do código também fazem referência a um arquivo `design.md`, mas esse arquivo não está presente no repositório.

## Configuração

A configuração pode ser fornecida por `appsettings.json`, arquivos específicos de ambiente ou variáveis de ambiente.

| Configuração | Variável de ambiente | Valor padrão |
|---|---|---|
| Connection string do PostgreSQL | `Postgres__ConnectionString` | `Host=localhost;Port=5432;Database=conversational_ai;Username=postgres;Password=postgres` |
| Endpoint OTLP | `Otel__OtlpEndpoint` | `http://localhost:4317` |
| Chave de assinatura JWT interna | `InternalAuth__SigningKey` | (vazio — obrigatório, mínimo 32 bytes) |
| Emissor do JWT | `InternalAuth__Issuer` | `conversational-ai-platform` |
| Audiência esperada | `InternalAuth__ServiceName` | `conversation-audit-service` |

Os timeouts de conexão e execução de comandos no PostgreSQL são limitados a cinco segundos. Dessa forma, falhas de banco são convertidas rapidamente em `503 Service Unavailable`.

## Executar localmente

### Pré-requisitos

- .NET 8 SDK.
- PostgreSQL com o schema e a tabela necessários.
- `InternalAuth__SigningKey` com pelo menos 32 bytes, igual ao configurado nos serviços que chamam este.
- Coletor compatível com OTLP, como Jaeger ou OpenTelemetry Collector, quando a exportação de traces for necessária.

### Iniciar o serviço

```bash
dotnet restore
dotnet run --launch-profile http
```

O perfil HTTP inicia a aplicação em:

```text
http://localhost:5021
```

O Swagger fica disponível no ambiente `Development` em:

```text
http://localhost:5021/swagger
```

O perfil HTTPS utiliza:

```text
https://localhost:7053
```

### Sobrescrever configurações

Linux ou macOS:

```bash
export Postgres__ConnectionString='Host=localhost;Port=5432;Database=conversational_ai;Username=postgres;Password=postgres'
export Otel__OtlpEndpoint='http://localhost:4317'
dotnet run --launch-profile http
```

PowerShell:

```powershell
$env:Postgres__ConnectionString = 'Host=localhost;Port=5432;Database=conversational_ai;Username=postgres;Password=postgres'
$env:Otel__OtlpEndpoint = 'http://localhost:4317'
dotnet run --launch-profile http
```

## Executar com Docker

### Criar a imagem

```bash
docker build -t conversation-audit-service .
```

### Iniciar o container

```bash
docker run --rm \
  --name conversation-audit-service \
  --publish 8080:8080 \
  --env Postgres__ConnectionString='Host=host.docker.internal;Port=5432;Database=conversational_ai;Username=postgres;Password=postgres' \
  --env Otel__OtlpEndpoint='http://host.docker.internal:4317' \
  conversation-audit-service
```

O endpoint ficará disponível em:

```text
http://localhost:8080/journey-events
```

No Linux, o acesso a serviços executados diretamente no host pode exigir:

```bash
--add-host=host.docker.internal:host-gateway
```

## Observabilidade

O serviço configura o OpenTelemetry com:

- Instrumentação de requisições do ASP.NET Core.
- Instrumentação das operações do Npgsql.
- Exportação de traces por OTLP.
- Nome do serviço `conversation-audit-service`.
- Correlação de `TraceId`, `SpanId` e `ParentId` nos logs de console.

O span do PostgreSQL é especialmente relevante porque a disponibilidade e a latência da persistência determinam se o endpoint retorna `202` ou `503`.

## Estrutura do projeto

```text
.
├── Adapters
│   ├── Inbound/Http
│   │   └── JourneyEventEndpoints.cs
│   └── Outbound/Persistence
│       └── PostgresJourneyEventRepository.cs
├── Application
│   ├── Ports
│   │   ├── Inbound
│   │   └── Outbound
│   └── UseCases
│       └── RecordJourneyEventUseCase.cs
├── Configuration
│   ├── OtelOptions.cs
│   └── PostgresOptions.cs
├── Domain
│   └── JourneyAuditEvent.cs
├── Platform
│   └── PlatformServices.cs
├── Program.cs
├── appsettings.json
├── Dockerfile
├── conversation-audit-service.csproj
└── conversation-audit-service.Tests/
```

## Testes

```bash
dotnet test
```

`conversation-audit-service.Tests` inclui testes de integração contra um PostgreSQL real via Testcontainers, montando o script de init real de `conversational-ai-demo-arch/database/conversational-ai-postgres-init.sql` (não um schema de teste à parte) — ver a nota de CI abaixo sobre por que isso exige um checkout adicional.

## CI

`.github/workflows/ci.yml` roda `dotnet build`/`dotnet test` a cada push/PR para `master`. Como os testes de repositório usam Testcontainers com o init script real do `conversational-ai-demo-arch`, o workflow faz um segundo `actions/checkout` desse repo (aninhado no workspace) antes de rodar os testes — sem isso, o teste falha com "Could not locate conversational-ai-postgres-init.sql".

## Limitações atuais

- O ator é sempre identificado como `conversation-orchestrator`; a ação é sempre `conversation.journey_processed` (ambos fixos no código).
- As migrações de banco são gerenciadas fora deste repositório (o schema/tabela usados em produção vêm do `conversational-ai-postgres-init.sql` de `conversational-ai-demo-arch`).
- Falhas de persistência não possuem retry (o chamador, `conversation-orchestrator`, é quem retenta via seu outbox).
- O endpoint não impõe um catálogo de valores permitidos para `intent` ou `outcome`.
- Não há validação explícita para timestamps futuros ou excessivamente antigos.

## Próximos passos recomendados

1. Tornar ator e ação deriváveis do contexto autenticado ou do evento, em vez de fixos.
2. Adicionar rate limiting.
3. Adicionar migrações de banco versionadas neste repositório.
4. Adicionar políticas de resiliência e métricas operacionais.
5. Validar o catálogo de eventos, intenções e resultados aceitos.
6. Definir uma política de retenção e proteção dos dados de auditoria.
