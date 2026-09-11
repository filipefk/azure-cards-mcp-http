using McpToolkit;

namespace AzureCardsMcpHttp;

// Decodifica o header x-api-key da requisição em curso (já validado pelo McpApiKeyMiddleware).
public sealed class HttpRequestContext(IHttpContextAccessor httpContextAccessor) : IRequestContext
{
    public string? AzureUrl => Read()?.AzureUrl;

    public string? AzureApiKey => Read()?.AzureApiKey;

    private McpApiKey? Read() =>
        McpApiKey.TryRead(httpContextAccessor.HttpContext, out var apiKey) ? apiKey : null;
}
