using GeraApiKey;

namespace AzureCardsMcpHttp.Endpoints;

// Endpoints desprotegidos (fora de /mcp) para gerar a API Key do header x-api-key e para desfazê-la.
// O corpo é um DTO com os campos nomeados; a ordem em que eles viram a lista do GeraApiKey
// fica em McpApiKey.ToValues/FromValues. São POST para os segredos irem no corpo, nunca na URL.
public static class ApiKeyEndpoints
{
    public sealed record GenerateApiKeyRequest(string? McpApiKey, string? AzureUrl, string? AzureApiKey);

    public sealed record GenerateApiKeyResponse(string ApiKey);

    public sealed record DecodeApiKeyRequest(string? ApiKey);

    public sealed record DecodeApiKeyResponse(string McpApiKey, string? AzureUrl, string? AzureApiKey);

    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api-key");

        group.MapPost("/generate", (GenerateApiKeyRequest request) =>
        {
            // Sem a chave MCP a key gerada nunca passaria no McpApiKeyMiddleware — falha aqui, com motivo.
            if (string.IsNullOrWhiteSpace(request.McpApiKey))
                return Results.BadRequest(new { error = "Informe o mcpApiKey." });

            var apiKey = new McpApiKey(request.McpApiKey, request.AzureUrl, request.AzureApiKey);
            try
            {
                return Results.Ok(new GenerateApiKeyResponse(ApiKeyGenerator.Generate(apiKey.ToValues())));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/decode", (DecodeApiKeyRequest request) =>
        {
            if (!ApiKeyGenerator.TryParse(request.ApiKey, out var values))
                return Results.BadRequest(new { error = "API Key inválida." });

            var apiKey = McpApiKey.FromValues(values);
            return Results.Ok(new DecodeApiKeyResponse(apiKey.McpKey, apiKey.AzureUrl, apiKey.AzureApiKey));
        });

        return app;
    }
}
