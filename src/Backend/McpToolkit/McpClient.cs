using Microsoft.Extensions.Options;
using ModelContextProtocol;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace McpToolkit;

public sealed class McpClient(HttpClient http, IOptions<McpOptions> options, IRequestContext requestContext)
{
    public const string ParentRelation = "System.LinkTypes.Hierarchy-Reverse";
    public const string ChildRelation = "System.LinkTypes.Hierarchy-Forward";

    private const string JsonMediaType = "application/json";
    private const string JsonPatchMediaType = "application/json-patch+json";
    private const int MaxIdsPerBatch = 200;

    private readonly McpOptions _opts = options.Value;

    private (string ProjectUrl, AuthenticationHeaderValue Authorization) ResolveTarget()
    {
        var url = requestContext.AzureUrl;
        if (string.IsNullOrWhiteSpace(url)) url = _opts.Url;

        var apiKey = requestContext.AzureApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) apiKey = _opts.ApiKey;

        if (string.IsNullOrWhiteSpace(url))
            throw new McpException("URL do projeto no Azure DevOps não informada (2º valor do header x-api-key, appsettings Azure:Url ou variável AZURE_URL_BOARD).");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new McpException("Chave de acesso do Azure DevOps não informada (3º valor do header x-api-key, appsettings Azure:ApiKey ou variável AZURE_USER_API_KEY).");

        return (NormalizeProjectUrl(url), BuildAuthorization(apiKey.Trim()));
    }

    // Aceita tanto a URL do projeto quanto qualquer URL do portal colada pelo usuário
    // (board, backlog, work item...): tudo a partir do primeiro segmento "/_" é descartado.
    public static string NormalizeProjectUrl(string url)
    {
        var normalized = url.Trim();
        var queryIndex = normalized.IndexOfAny(['?', '#']);
        if (queryIndex >= 0) normalized = normalized[..queryIndex];
        var areaIndex = normalized.IndexOf("/_", StringComparison.Ordinal);
        if (areaIndex >= 0) normalized = normalized[..areaIndex];
        normalized = normalized.TrimEnd('/');

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new McpException($"URL do Azure DevOps inválida: '{url}'.");

        if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            throw new McpException("A URL do Azure DevOps precisa incluir organização e projeto (ex: https://dev.azure.com/{organizacao}/{projeto}).");

        return normalized;
    }

    // PAT vai como Basic (":" + PAT em base64), conforme a documentação; um token do Microsoft Entra (JWT) vai como Bearer.
    private static AuthenticationHeaderValue BuildAuthorization(string apiKey) =>
        LooksLikeJwt(apiKey)
            ? new AuthenticationHeaderValue("Bearer", apiKey)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{apiKey}")));

    private static bool LooksLikeJwt(string token) =>
        token.StartsWith("eyJ", StringComparison.Ordinal) && token.Count(c => c == '.') == 2;

    private async Task<JsonNode?> SendAsync(HttpMethod method, string pathAndQuery, JsonNode? body = null,
        string mediaType = JsonMediaType, string? apiVersion = null, CancellationToken ct = default)
    {
        var (projectUrl, authorization) = ResolveTarget();
        var separator = pathAndQuery.Contains('?') ? '&' : '?';
        var uri = new Uri($"{projectUrl}/_apis/{pathAndQuery}{separator}api-version={apiVersion ?? _opts.ApiVersion}");

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, mediaType);

        using var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        // Com credencial inválida (ou organização/projeto inexistente) o Azure DevOps pode responder
        // com a página HTML de login — às vezes com status 203 — em vez de um erro JSON.
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            throw new AzureDevOpsApiException(
                response.IsSuccessStatusCode ? HttpStatusCode.Unauthorized : response.StatusCode,
                "A API devolveu uma página HTML em vez de JSON — normalmente chave de acesso inválida/expirada ou URL de organização/projeto incorreta.");

        if (!response.IsSuccessStatusCode)
            throw new AzureDevOpsApiException(response.StatusCode, content);

        return string.IsNullOrWhiteSpace(content) ? null : JsonNode.Parse(content);
    }

    private static string BuildQuery(params (string Key, string? Value)[] parameters)
    {
        var parts = parameters
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}");
        var query = string.Join("&", parts);
        return query.Length == 0 ? string.Empty : $"?{query}";
    }

    public string WorkItemApiUrl(int id) => $"{ResolveTarget().ProjectUrl}/_apis/wit/workItems/{id}";

    public string WorkItemWebUrl(int id) => $"{ResolveTarget().ProjectUrl}/_workitems/edit/{id}";

    public JsonObject BuildParentRelationOperation(int parentId) => new()
    {
        ["op"] = "add",
        ["path"] = "/relations/-",
        ["value"] = new JsonObject
        {
            ["rel"] = ParentRelation,
            ["url"] = WorkItemApiUrl(parentId)
        }
    };

    // ---------- Work items ----------

    public Task<JsonNode?> GetWorkItemAsync(int id, bool expandRelations = true, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"wit/workitems/{id}{BuildQuery(("$expand", expandRelations ? "relations" : null))}", ct: ct);

    // A API aceita no máximo 200 IDs por chamada; lotes maiores são fatiados. IDs inexistentes são omitidos.
    public async Task<JsonArray> GetWorkItemsAsync(IReadOnlyCollection<int> ids, bool expandRelations = false, CancellationToken ct = default)
    {
        var result = new JsonArray();
        foreach (var chunk in ids.Distinct().Chunk(MaxIdsPerBatch))
        {
            var query = BuildQuery(
                ("ids", string.Join(",", chunk)),
                ("$expand", expandRelations ? "relations" : null),
                ("errorPolicy", "omit"));
            var page = await SendAsync(HttpMethod.Get, $"wit/workitems{query}", ct: ct);
            foreach (var item in (page?["value"] as JsonArray ?? []).OfType<JsonObject>())
                result.Add(item.DeepClone());
        }
        return result;
    }

    public Task<JsonNode?> CreateWorkItemAsync(string type, JsonArray operations, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"wit/workitems/${Uri.EscapeDataString(type)}?$expand=relations", operations, JsonPatchMediaType, ct: ct);

    public Task<JsonNode?> UpdateWorkItemAsync(int id, JsonArray operations, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Patch, $"wit/workitems/{id}?$expand=relations", operations, JsonPatchMediaType, ct: ct);

    // Troca (ou remove, com parentId nulo) o pai de um work item num único PATCH: remove as relações
    // Hierarchy-Reverse existentes e adiciona a nova. Se o pai pedido já for o atual, não altera nada.
    public async Task<JsonNode?> SetParentAsync(int id, int? parentId, CancellationToken ct = default)
    {
        var current = await GetWorkItemAsync(id, true, ct) as JsonObject
            ?? throw new McpException($"Work item {id} não encontrado.");

        var relations = current["relations"] as JsonArray ?? [];
        var indexesToRemove = new List<int>();
        var alreadyLinked = false;
        for (var i = 0; i < relations.Count; i++)
        {
            if (relations[i]?["rel"]?.GetValue<string>() != ParentRelation) continue;

            var url = relations[i]?["url"]?.GetValue<string>() ?? string.Empty;
            if (parentId is int requested && url.EndsWith($"/{requested}", StringComparison.Ordinal))
                alreadyLinked = true;
            else
                indexesToRemove.Add(i);
        }

        var operations = new JsonArray();
        // De trás para frente: cada remoção desloca os índices das relações seguintes.
        foreach (var index in indexesToRemove.OrderDescending())
            operations.Add(new JsonObject { ["op"] = "remove", ["path"] = $"/relations/{index}" });
        if (parentId is int newParent && !alreadyLinked)
            operations.Add(BuildParentRelationOperation(newParent));

        return operations.Count == 0 ? current : await UpdateWorkItemAsync(id, operations, ct);
    }

    // ---------- Consultas ----------

    public Task<JsonNode?> QueryByWiqlAsync(string query, int? top, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"wit/wiql{BuildQuery(("$top", top?.ToString()))}", new JsonObject { ["query"] = query }, ct: ct);

    public Task<JsonNode?> ListWorkItemTypesAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "wit/workitemtypes", ct: ct);

    public Task<JsonNode?> ListWorkItemTypeStatesAsync(string type, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"wit/workitemtypes/{Uri.EscapeDataString(type)}/states", ct: ct);

    // ---------- Comentários ----------

    public Task<JsonNode?> ListCommentsAsync(int id, int top, string? continuationToken, CancellationToken ct = default)
    {
        var query = BuildQuery(("$top", top.ToString()), ("continuationToken", continuationToken));
        return SendAsync(HttpMethod.Get, $"wit/workItems/{id}/comments{query}", apiVersion: _opts.CommentsApiVersion, ct: ct);
    }

    public Task<JsonNode?> AddCommentAsync(int id, string text, string format, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"wit/workItems/{id}/comments{BuildQuery(("format", format))}",
            new JsonObject { ["text"] = text }, apiVersion: _opts.CommentsApiVersion, ct: ct);
}
