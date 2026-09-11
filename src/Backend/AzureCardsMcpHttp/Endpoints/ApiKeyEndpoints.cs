using GeraApiKey;

namespace AzureCardsMcpHttp.Endpoints;

// Endpoints desprotegidos (fora de /mcp) para gerar uma API Key a partir de uma lista de valores e para desfazê-la.
// São POST para os valores (segredos) irem no corpo, nunca na URL.
public static class ApiKeyEndpoints
{
    public sealed record GenerateApiKeyRequest(List<string>? Values);

    public sealed record GenerateApiKeyResponse(string ApiKey);

    public sealed record DecodeApiKeyRequest(string? ApiKey);

    public sealed record DecodeApiKeyResponse(IReadOnlyList<string> Values);

    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api-key");

        group.MapPost("/generate", (GenerateApiKeyRequest request) =>
        {
            try
            {
                return Results.Ok(new GenerateApiKeyResponse(ApiKeyGenerator.Generate(request.Values ?? [])));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/decode", (DecodeApiKeyRequest request) =>
            ApiKeyGenerator.TryParse(request.ApiKey, out var values)
                ? Results.Ok(new DecodeApiKeyResponse(values))
                : Results.BadRequest(new { error = "API Key inválida." }));

        return app;
    }
}
