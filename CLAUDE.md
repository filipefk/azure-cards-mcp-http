# CLAUDE.md

Instruções para o Claude Code ao trabalhar neste repositório.

## Visão Geral

AzureCardsMcpHttp é um servidor MCP (Model Context Protocol) em .NET 10 ASP.NET Core que expõe a API REST de Work Item Tracking do Azure DevOps (cards/work items, consultas WIQL e comentários) como tools MCP via transporte HTTP, montado em `/mcp` e protegido por um único header `x-api-key` (pensado para conectores que só aceitam um cabeçalho, como Copilot Studio e Claude Web). Foi criado a partir do template `ModeloMcpHttp` (`dotnet new ffkmcphttp`) e segue a mesma organização do `n8n-mcp-http`. Substitui os scripts curl da skill `azure-card` (growdev-board-plugin) por tools tipados.

Veja [README.md](README.md) para a documentação voltada ao usuário (configuração, lista de tools, como registrar no Claude Code).

## Build

Nunca execute `dotnet build`/`dotnet run` automaticamente após editar código, nem inclua isso como etapa de um plano — regra global do usuário. Só compile quando pedido explicitamente. Quando pedido:

```bash
dotnet build AzureCardsMcpHttp.slnx
dotnet run --project src/Backend/AzureCardsMcpHttp/AzureCardsMcpHttp.csproj
```

Não há projeto de testes.

## Arquitetura

```
src/Backend/McpToolkit/          — biblioteca McpToolkit (tools + cliente da API)
src/Backend/GeraApiKey/          — biblioteca GeraApiKey (lista de strings ⇄ API Key única)
src/Backend/AzureCardsMcpHttp/   — host ASP.NET Core
```

- **GeraApiKey** (`Microsoft.NET.Sdk`, sem pacotes) — `ApiKeyGenerator` estático e genérico: `Generate(IEnumerable<string>)` junta os valores com `Separator` (U+001F, não digitável; valor que o contenha ou nulo → `ArgumentException`), prefixa 1 byte de semente aleatória, faz XOR com um keystream xorshift32 derivado da semente e codifica em Base64Url (seguro para header). `Parse`/`TryParse` desfazem e devolvem os valores na mesma ordem (UTF-8 estrito; entrada inválida → `FormatException`/`false`). **É ofuscação, não criptografia** — não há segredo.

- **McpToolkit** (`Microsoft.NET.Sdk`, sem dependência de ASP.NET Core — só `Microsoft.Extensions.Options` e o SDK MCP `ModelContextProtocol.Core`):
  - `McpClient.cs` — o único lugar que fala com a API do Azure DevOps. Monta as URLs como `{projectUrl}/_apis/{área}/{recurso}?api-version=...`, autentica, envia e devolve `JsonNode?` bruto. Métodos: `GetWorkItemAsync`, `GetWorkItemsAsync` (fatia em lotes de 200, `errorPolicy=omit`), `CreateWorkItemAsync`/`UpdateWorkItemAsync` (JSON Patch, `application/json-patch+json`, com `$expand=relations`), `SetParentAsync` (composto: GET → remove relações `Hierarchy-Reverse` existentes de trás para frente → adiciona a nova, num único PATCH; não faz nada se o pai pedido já é o atual), `QueryByWiqlAsync`, `ListWorkItemTypesAsync`, `ListWorkItemTypeStatesAsync`, `ListCommentsAsync`/`AddCommentAsync` (usam `CommentsApiVersion`, `7.1-preview.4`). Também expõe `WorkItemApiUrl` (usada no `url` das relações), `WorkItemWebUrl` (link do portal) e `BuildParentRelationOperation`.
  - `AzureDevOpsApiException.cs` — erro não-2xx da API (`StatusCode` + `Body`). **Herda de `McpException`** de propósito: o SDK só repassa ao cliente a mensagem de `McpException`; qualquer outro tipo vira um erro genérico. A mensagem usa o campo `message` do JSON de erro do Azure DevOps quando existir.
  - `IRequestContext.cs` — expõe `AzureUrl`/`AzureApiKey` da requisição HTTP em curso (implementado no host por `HttpRequestContext`, que decodifica o `x-api-key`), para a biblioteca não depender de `IHttpContextAccessor` nem de `GeraApiKey`.
  - `McpOptions.cs` — seção `Azure` (`Url`, `ApiKey`, `ApiVersion`, `CommentsApiVersion`, `IgnoreSslErrors`).
  - `WorkItemTools.cs`, `QueryTools.cs`, `CommentTools.cs` — a superfície MCP, uma classe `[McpServerToolType]` por família, cada uma ligável/desligável via `Tools:WorkItems:Enabled`, `Tools:Queries:Enabled`, `Tools:Comments:Enabled` (padrão `true`).
  - `McpToolsHelpers.cs` — helpers compartilhados: `ResolveWorkItemId` (aceita número, `#123` ou URL do card), `DescriptionFieldFor`, montagem das operações JSON Patch (`BuildCreateOperations`, `AddField`, `AddCustomFields`), `ParseSpec` (objetos de `create_work_item_tree`), `StripHtml`, `FormatIdentity`, `EscapeWiql` e os resumos (`SummarizeListItem`, `SummarizeWorkItem`, `SummarizeCreated`).
  - `Shell/` — tools de execução de comandos do SO herdadas do template (`execute_shell_command`, `get_shell_info`), **desligadas por padrão** (`Shell:Enabled = false`).
- **AzureCardsMcpHttp** (`Microsoft.NET.Sdk.Web`, referencia `McpToolkit` e `GeraApiKey`) — `Program.cs` configura Serilog, `McpOptions` (com fallback para as variáveis da skill, ver abaixo), `McpAuthOptions` (seção `McpAuth`), `IRequestContext`, o `McpClient` via `AddHttpClient` (respeitando `Azure:IgnoreSslErrors`), registra as famílias de tools condicionalmente, mapeia os endpoints de API Key e `/mcp`.
  - `McpApiKey.cs` — `HeaderName = "x-api-key"` e `TryRead(HttpContext)`, que decodifica o header em `McpKey`, `AzureUrl`, `AzureApiKey` (posições 0/1/2; vazias ou ausentes → `null`). Usado pelo middleware de autenticação e pelo `HttpRequestContext`.
  - `Middleware/McpApiKeyMiddleware.cs` — em todo `/mcp` (GET/POST/DELETE): header ausente/indecodificável, `McpAuth:ApiKeys` vazia (fail-closed) ou `McpKey` fora da lista → `401` com `{ "error": ... }`; compara em tempo constante (`CryptographicOperations.FixedTimeEquals`) e loga o motivo como aviso.
  - `Middleware/McpTrafficLoggingMiddleware.cs` — roda antes da autenticação; loga corpo e headers de `POST /mcp`, redigindo `x-api-key`, `Authorization` e o campo `password`; `GET /mcp` (canal SSE) não é bufferizado.
  - `Endpoints/ApiKeyEndpoints.cs` — `POST /api-key/generate` (`{ "values": [...] }` → `{ "apiKey" }`) e `POST /api-key/decode` (`{ "apiKey" }` → `{ "values": [...] }`), **desprotegidos** (fora de `/mcp`), erro de entrada → `400`. POST para os segredos não irem na URL.

## Destino e autenticação

O header `x-api-key` é a única entrada do cliente: uma string do `GeraApiKey` com `[mcp_api_key, url do projeto, chave do Azure]`. O `mcp_api_key` é validado pelo `McpApiKeyMiddleware` contra `McpAuth:ApiKeys`; URL e chave do Azure são opcionais. Os antigos headers `X-AZURE-URL` / `X-AZURE-API-KEY` não são mais lidos.

`McpClient.ResolveTarget()` resolve a cada requisição, nesta precedência:

1. `IRequestContext.AzureUrl` / `AzureApiKey` (2º e 3º valores do `x-api-key`);
2. `Azure:Url` / `Azure:ApiKey` da configuração;
3. Variáveis `AZURE_URL_BOARD` / `AZURE_USER_API_KEY` (as mesmas da skill `azure-card`) — aplicadas via `PostConfigure<McpOptions>` no `Program.cs` quando os valores do passo 2 estão vazios.

A URL é a do **projeto** (`https://dev.azure.com/{org}/{projeto}`). `NormalizeProjectUrl` descarta query string e tudo a partir do primeiro `/_`, então uma URL de board/backlog/work item colada também funciona; em `dev.azure.com` exige organização e projeto.

Autenticação: chave com cara de JWT (`eyJ...` com 3 segmentos) vai como `Bearer` (token do Microsoft Entra); qualquer outra é tratada como PAT e vai como `Basic base64(":" + PAT)`. Resposta `text/html` (tela de login que o Azure DevOps devolve, às vezes com 203, quando a credencial ou a URL está errada) vira `AzureDevOpsApiException` com mensagem explicativa, em vez de falhar no parse do JSON.

## Regras de domínio

- **Bug não tem `System.Description`**: o conteúdo descritivo vai em `Microsoft.VSTS.TCM.ReproSteps`. `DescriptionFieldFor(type)` decide o campo em `create_work_item`, `create_work_item_tree`, `update_work_item` (que faz um GET antes para saber o tipo quando `description` é informada) e nos resumos (`descriptionField` informa qual campo foi lido).
- **Hierarquia**: `System.LinkTypes.Hierarchy-Reverse` = "este item é filho de"; `Hierarchy-Forward` = filho. O pai é lido de `System.Parent` e, na falta, das relações.
- **Regras de apresentação ficam no cliente**: sufixo `(Criado pelo Claude Code)` no título, modelos HTML de User Story/Bug/Task e a confirmação antes de criar são responsabilidade da skill/cliente, não do servidor.
- **Busca**: `search_work_items` monta WIQL com `EscapeWiql` restrita a `[System.TeamProject] = @project`; o filtro de "abertos" exclui por nome os estados `Closed`, `Done`, `Removed`, `Completed` (WIQL não expõe a categoria do estado). Ambas as consultas pedem `top + 1` à WIQL para calcular `truncated` e buscam os detalhes em lote.

## Economia de tokens

- `get_work_item` devolve um resumo com a descrição em texto puro (`StripHtml`); campos completos, HTML e relações só com `full=true`.
- Buscas devolvem um resumo compacto por card (`SummarizeListItem`) — sem descrição.
- `create_work_item_tree` cria pai + filhos numa só chamada e reporta falhas por filho em `errors`, sem abortar os demais.
- Todo parâmetro de card (`work_item_id`, `parent_id`, `work_item_ids`) aceita número ou URL.

## Convenções

- `Nullable` e `ImplicitUsings` habilitados; namespaces file-scoped; primary constructors para DI (inclusive nos tools).
- Tools: `[McpServerTool(Name = "snake_case", Title, ReadOnly, Destructive, Idempotent, OpenWorld = false)]`, métodos PascalCase, parâmetros em snake_case, descrições em pt-BR, retorno `Task<string>` com JSON. Parâmetros estruturados são `JsonObject`/`JsonArray` (o SDK gera o schema `object`/`array`).
- Erros dentro de tools: lance `McpException` (ou `AzureDevOpsApiException`) — outras exceções chegam ao cliente como erro genérico.
- Sem comentários `<summary>` de doc XML (regra global do usuário).
- **Atenção com `ModelContextProtocol.Core` em class library comum:** `Microsoft.Extensions.Options` precisa estar referenciado explicitamente no `McpToolkit.csproj`; se um organizador de usings remover `using Microsoft.Extensions.Options;` o build quebra com `CS0246 (IOptions<>)`.
