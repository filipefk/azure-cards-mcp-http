using GeraApiKey;
using System.Diagnostics.CodeAnalysis;

namespace AzureCardsMcpHttp;

// Conteúdo do header x-api-key, gerado pelo GeraApiKey.ApiKeyGenerator a partir de
// [mcp_api_key, URL do projeto no Azure DevOps, chave do Azure DevOps] — nesta ordem.
// URL e chave do Azure são opcionais: vazias ou ausentes caem no fallback do McpClient (appsettings/variáveis).
public sealed class McpApiKey(string mcpKey, string? azureUrl, string? azureApiKey)
{
    public const string HeaderName = "x-api-key";

    public string McpKey { get; } = mcpKey;
    public string? AzureUrl { get; } = azureUrl;
    public string? AzureApiKey { get; } = azureApiKey;

    public static bool TryRead(HttpContext? context, [NotNullWhen(true)] out McpApiKey? apiKey)
    {
        apiKey = null;
        var header = context?.Request.Headers[HeaderName].FirstOrDefault();
        if (!ApiKeyGenerator.TryParse(header, out var values)) return false;

        apiKey = new McpApiKey(values[0], ValueAt(values, 1), ValueAt(values, 2));
        return true;
    }

    private static string? ValueAt(IReadOnlyList<string> values, int index) =>
        index < values.Count && !string.IsNullOrWhiteSpace(values[index]) ? values[index] : null;
}
