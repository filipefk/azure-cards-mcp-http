using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace AzureCardsMcpHttp.Middleware;

// Protege /mcp: exige o header x-api-key e valida o mcp_api_key (primeiro valor da key) contra McpAuth:ApiKeys.
// Lista vazia rejeita tudo (fail-closed).
public sealed class McpApiKeyMiddleware(RequestDelegate next, IOptionsMonitor<McpAuthOptions> options, ILogger<McpApiKeyMiddleware> logger)
{
    private const string UnauthorizedMessage = "Não autorizado: informe um header x-api-key válido.";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!McpApiKey.TryRead(context, out var apiKey))
        {
            await RejectAsync(context, $"header {McpApiKey.HeaderName} ausente ou inválido");
            return;
        }

        var authorizedKeys = options.CurrentValue.ApiKeys.Where(k => !string.IsNullOrEmpty(k)).ToList();
        if (authorizedKeys.Count == 0)
        {
            await RejectAsync(context, "nenhuma chave cadastrada em McpAuth:ApiKeys");
            return;
        }

        if (!IsAuthorized(apiKey.McpKey, authorizedKeys))
        {
            await RejectAsync(context, "mcp_api_key não autorizada");
            return;
        }

        await next(context);
    }

    // Compara com todas as chaves em tempo constante, sem sair na primeira coincidência.
    private static bool IsAuthorized(string mcpKey, IEnumerable<string> authorizedKeys)
    {
        var candidate = Encoding.UTF8.GetBytes(mcpKey);
        var authorized = false;
        foreach (var key in authorizedKeys)
            authorized |= CryptographicOperations.FixedTimeEquals(candidate, Encoding.UTF8.GetBytes(key));
        return authorized;
    }

    private Task RejectAsync(HttpContext context, string reason)
    {
        logger.LogWarning("MCP request rejeitada: {Method} {Path} | Motivo: {Reason}", context.Request.Method, context.Request.Path, reason);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { error = UnauthorizedMessage });
    }
}
