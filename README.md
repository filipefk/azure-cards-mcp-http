# Azure Cards MCP HTTP

Servidor **MCP (Model Context Protocol) via HTTP** em .NET 10 para manipular cards (work items) do **Azure DevOps Boards**: ler, criar (inclusive pai + filhos), atualizar, vincular pai, buscar (filtros ou WIQL) e comentar.

Construído sobre o template `ModeloMcpHttp` com o SDK oficial `ModelContextProtocol.AspNetCore`, usando a [API REST do Azure DevOps](https://learn.microsoft.com/azure/devops/integrate/how-to/call-rest-api) (`api-version=7.1`).

## Estrutura

```
AzureCardsMcpHttp.slnx
└── src/Backend/
    ├── McpToolkit/                     # tools MCP + cliente da API
    │   ├── McpClient.cs                 # chamadas REST ao Azure DevOps
    │   ├── WorkItemTools.cs             # ler / criar / atualizar / vincular pai
    │   ├── QueryTools.cs                # busca, WIQL, tipos e estados
    │   ├── CommentTools.cs              # comentários
    │   ├── McpToolsHelpers.cs           # resumos, JSON Patch, IDs a partir de URL
    │   ├── McpOptions.cs / IRequestContext.cs / AzureDevOpsApiException.cs
    │   └── Shell/                       # execução de comandos do SO (desligado por padrão)
    ├── GeraApiKey/                     # gera/desfaz a API Key única (lista de strings ⇄ string)
    │   └── ApiKeyGenerator.cs
    └── AzureCardsMcpHttp/              # host ASP.NET Core (Program.cs, middlewares, endpoints, appsettings)
```

## Rodando

```bash
dotnet run --project src/Backend/AzureCardsMcpHttp/AzureCardsMcpHttp.csproj
```

O endpoint MCP fica em `http://localhost:5464/mcp` (perfil `http` do `launchSettings.json`).

## Configuração

### Header `x-api-key`

Toda chamada a `/mcp` precisa de **um único header**, `x-api-key` — pensado para conectores que só aceitam um cabeçalho (Copilot Studio, Claude Web). Ele é uma única string que carrega três valores, nesta ordem:

1. `mcp_api_key` — chave de acesso ao MCP, validada contra `McpAuth:ApiKeys` do `appsettings.json`;
2. URL do projeto no Azure DevOps (opcional);
3. chave do Azure DevOps — PAT ou token do Entra (opcional).

Sem o header, com uma key que não decodifica, com `mcp_api_key` fora da lista ou com a lista **vazia**, a resposta é `401`.

A key é gerada pela biblioteca `GeraApiKey`: os valores são unidos por um caractere não digitável (U+001F), embaralhados (XOR com semente aleatória) e codificados em Base64Url — só `A-Z a-z 0-9 - _`, seguro para header. **É ofuscação, não criptografia**: quem tem a key recupera os valores (inclusive o PAT). Trate a key como um segredo.

#### Gerando e desfazendo a key

Dois endpoints **sem autenticação** (fora de `/mcp`):

```bash
curl -X POST http://localhost:5464/api-key/generate -H "Content-Type: application/json" -d "{\"mcpApiKey\":\"minha-chave-mcp\",\"azureUrl\":\"https://dev.azure.com/minha-org/meu-projeto\",\"azureApiKey\":\"<seu-PAT>\"}"
```

```bash
curl -X POST http://localhost:5464/api-key/decode -H "Content-Type: application/json" -d "{\"apiKey\":\"<key>\"}"
```

`generate` recebe o DTO `{ "mcpApiKey": "...", "azureUrl": "...", "azureApiKey": "..." }` e devolve `{ "apiKey": "..." }`; `decode` faz o inverso e devolve os três campos. Só `mcpApiKey` é obrigatório (sem ele, `400`): `azureUrl` e `azureApiKey` omitidos ou vazios saem como `null` no `decode` e fazem o servidor usar o Azure da configuração. Os mesmos valores geram keys diferentes a cada chamada (semente aleatória), todas válidas.

### Projeto e chave de acesso

O servidor resolve o destino **a cada requisição**, nesta ordem:

| Prioridade | URL do projeto | Chave de acesso |
|---|---|---|
| 1 | 2º valor do header `x-api-key` | 3º valor do header `x-api-key` |
| 2 | `Azure:Url` (appsettings / env `Azure__Url`) | `Azure:ApiKey` (appsettings / env `Azure__ApiKey`) |
| 3 | variável `AZURE_URL_BOARD` | variável `AZURE_USER_API_KEY` |

Uma key gerada só com a chave MCP (ou com URL/chave vazias) usa o Azure configurado no servidor. O passo 3 usa as mesmas variáveis da skill `azure-card`.

- **URL**: a URL do projeto, ex. `https://dev.azure.com/minha-org/meu-projeto`. Uma URL de board, backlog ou card colada do navegador também serve — tudo a partir de `/_` é descartado. Para Azure DevOps Server: `https://servidor/Colecao/Projeto`.
- **Chave**: um **PAT** (enviado como `Authorization: Basic`) com escopo *Work Items (Read & Write)*, ou um token do **Microsoft Entra ID** (JWT, enviado como `Bearer`). O tipo é detectado automaticamente.

Evite gravar a chave no `appsettings.json`; prefira variável de ambiente, user secrets ou o header.

### `appsettings.json`

```json
{
  "McpAuth": {
    "ApiKeys": [ "minha-chave-mcp" ]
  },
  "Azure": {
    "Url": "",
    "ApiKey": "",
    "ApiVersion": "7.1",
    "CommentsApiVersion": "7.1-preview.4",
    "IgnoreSslErrors": false
  },
  "Tools": {
    "WorkItems": { "Enabled": true },
    "Queries": { "Enabled": true },
    "Comments": { "Enabled": true }
  },
  "Shell": { "Enabled": false }
}
```

- `McpAuth:ApiKeys` — chaves MCP aceitas (1º valor do `x-api-key`). Lista vazia rejeita todas as chamadas a `/mcp`. Via variável de ambiente: `McpAuth__ApiKeys__0`, `McpAuth__ApiKeys__1`...
- `ApiVersion` / `CommentsApiVersion` — ajuste para versões antigas do Azure DevOps Server.
- `IgnoreSslErrors` — aceita certificado TLS inválido/autoassinado (Azure DevOps Server interno).
- `Tools:*:Enabled` — desliga uma família inteira de tools (ela nem aparece no `tools/list`).
- `Shell:Enabled` — tools `execute_shell_command` / `get_shell_info` herdadas do template; desligadas por padrão. Se ligar, configure ao menos `Shell:Password` (veja as opções em `ShellOptions`).

## Registrando no Claude Code

Gere a key em `/api-key/generate` (com os três campos, ou só o `mcpApiKey` para usar o Azure configurado no servidor) e registre:

```bash
claude mcp add --transport http azure-cards http://localhost:5464/mcp --header "x-api-key: <key>"
```

Em conectores web (Copilot Studio, Claude Web), cadastre o mesmo header `x-api-key` com a key gerada.

## Tools

Todo parâmetro de card (`work_item_id`, `parent_id`, `work_item_ids`) aceita o número (`123`, `#123`) ou a URL do card.

### Cards (`WorkItemTools`)

| Tool | O que faz |
|---|---|
| `get_work_item` | Resumo do card: tipo, título, estado, responsável, área, iteração, tags, pai, filhos, URL e descrição em texto puro. `full=true` inclui todos os campos, HTML e relações. |
| `get_work_items` | Vários cards de uma vez (IDs separados por vírgula). |
| `create_work_item` | Cria um card. `parent_id` cria já como filho de um card existente. Aceita responsável, estado, área, iteração, tags e campos extras (`fields`). |
| `create_work_item_tree` | Cria um pai e seus filhos numa única chamada (ex: User Story + Tasks). Filho sem `type` vira `Task`; falha de um filho não interrompe os demais. |
| `update_work_item` | Altera título, descrição, estado, responsável, área, iteração, tags e campos extras. Omitido = não altera; `""` = limpa. |
| `set_work_item_parent` | Vincula, troca ou (sem `parent_id`) remove o pai. |

A descrição vai automaticamente no campo certo: **Repro Steps** (`Microsoft.VSTS.TCM.ReproSteps`) para Bug e **Description** para os demais tipos. O conteúdo é HTML (o Azure DevOps renderiza HTML nesses campos).

### Busca (`QueryTools`)

| Tool | O que faz |
|---|---|
| `search_work_items` | Filtros combináveis: tipo, estado, responsável (`@me` ou `""` para sem responsável), texto no título, área, iteração, tag, pai. Por padrão ignora Closed/Done/Removed/Completed. |
| `query_wiql` | Executa WIQL livre (inclusive consultas de links/árvore, que retornam `relations`). |
| `list_work_item_types` | Tipos de work item do projeto. |
| `list_work_item_states` | Estados válidos de um tipo, com categoria. |

### Comentários (`CommentTools`)

| Tool | O que faz |
|---|---|
| `list_comments` | Comentários do card (texto puro, autor, data), paginados por `continuation_token`. |
| `add_comment` | Adiciona comentário em HTML (padrão) ou Markdown. |

## Logs

Serilog em console e `logs/log.txt` (rotação diária). O middleware de tráfego registra as chamadas `POST /mcp` com os headers `x-api-key` e `Authorization` e o campo `password` redigidos (`***`). Chamadas rejeitadas por falta de autorização são logadas como aviso com o motivo (sem a chave).

## Requisitos

- .NET 10 SDK
