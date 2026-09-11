namespace McpToolkit;

// Abstrai o acesso à requisição HTTP em curso, para que o McpToolkit não dependa diretamente
// de Microsoft.AspNetCore.Http nem do formato do header x-api-key (implementado no host, AzureCardsMcpHttp).
public interface IRequestContext
{
    string? AzureUrl { get; }
    string? AzureApiKey { get; }
}
