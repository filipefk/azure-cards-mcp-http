using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;
using static McpToolkit.McpToolsHelpers;

namespace McpToolkit;

[McpServerToolType]
public sealed class CommentTools(McpClient azure)
{
    private static readonly string[] Formats = ["html", "markdown"];

    [McpServerTool(Name = "list_comments", Title = "Listar comentários", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Lista os comentários (discussão) de um card, com o texto em texto puro, autor e data. Resposta paginada: se vier 'continuationToken', chame de novo passando-o para obter a próxima página.")]
    public async Task<string> ListComments(
        [Description("ID ou URL do card")] string work_item_id,
        [Description("Quantidade máxima de comentários (padrão 20, máximo 200)")] int top = 20,
        [Description("Token de continuação retornado por uma chamada anterior (opcional)")] string? continuation_token = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkItemId(work_item_id);
        var result = await azure.ListCommentsAsync(id, Math.Clamp(top, 1, MaxTop), continuation_token, ct) as JsonObject;

        return Json(new JsonObject
        {
            ["workItemId"] = id,
            ["totalCount"] = result?["totalCount"]?.DeepClone(),
            ["comments"] = new JsonArray([.. (result?["comments"] as JsonArray ?? []).OfType<JsonObject>().Select(c => (JsonNode)SummarizeComment(c))]),
            ["continuationToken"] = result?["continuationToken"]?.DeepClone()
        });
    }

    [McpServerTool(Name = "add_comment", Title = "Adicionar comentário", Destructive = false, OpenWorld = false),
     Description("Adiciona um comentário na discussão de um card. O texto pode ser HTML (padrão) ou Markdown.")]
    public async Task<string> AddComment(
        [Description("ID ou URL do card")] string work_item_id,
        [Description("Texto do comentário")] string text,
        [Description("Formato do texto: 'html' (padrão) ou 'markdown'")] string format = "html",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new McpException("Informe o texto do comentário.");

        var normalizedFormat = format.Trim().ToLowerInvariant();
        if (!Formats.Contains(normalizedFormat))
            throw new McpException($"Formato '{format}' inválido. Use 'html' ou 'markdown'.");

        var id = ResolveWorkItemId(work_item_id);
        var created = await azure.AddCommentAsync(id, text, normalizedFormat, ct) as JsonObject;
        var summary = SummarizeComment(created);
        summary["workItemId"] = id;
        return Json(summary);
    }

    private static JsonObject SummarizeComment(JsonObject? comment) => new()
    {
        ["id"] = comment?["id"]?.DeepClone(),
        ["text"] = StripHtml(comment?["text"]?.GetValue<string>()),
        ["createdBy"] = FormatIdentity(comment?["createdBy"]),
        ["createdDate"] = comment?["createdDate"]?.DeepClone(),
        ["modifiedDate"] = comment?["modifiedDate"]?.DeepClone()
    };
}
