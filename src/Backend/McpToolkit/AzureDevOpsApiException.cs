using ModelContextProtocol;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpToolkit;

// Herda de McpException para que a mensagem (status + erro da API) chegue ao cliente MCP;
// o SDK esconde a mensagem de qualquer outro tipo de exceção lançada por um tool.
public sealed class AzureDevOpsApiException(HttpStatusCode statusCode, string body)
    : McpException($"Azure DevOps API {(int)statusCode}: {Describe(body)}")
{
    private const int MaxBodyChars = 2000;

    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Body { get; } = body;

    // Os erros da API vêm como {"message":"TF401232: ...", "typeName": ...}; quando for o caso,
    // devolve só a mensagem em vez do JSON inteiro.
    private static string Describe(string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["message"] is JsonValue value && value.TryGetValue<string>(out var message) && message.Length > 0)
                return message;
        }
        catch (JsonException)
        {
        }

        return body.Length <= MaxBodyChars ? body : body[..MaxBodyChars] + "... (truncado)";
    }
}
