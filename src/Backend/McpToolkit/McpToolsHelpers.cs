using ModelContextProtocol;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace McpToolkit;

internal sealed record WorkItemSpec(
    string Type,
    string Title,
    string? Description,
    string? AssignedTo,
    string? State,
    string? AreaPath,
    string? IterationPath,
    string? Tags,
    JsonObject? Fields);

// Helpers compartilhados entre as classes de tools MCP (WorkItemTools, QueryTools, CommentTools).
internal static partial class McpToolsHelpers
{
    public const string DescriptionField = "System.Description";
    public const string ReproStepsField = "Microsoft.VSTS.TCM.ReproSteps";
    public const int MaxTop = 200;

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|h[1-6]|tr|ul|ol)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]*\n[ \t]*")]
    private static partial Regex LineSpacesRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLinesRegex();

    [GeneratedRegex(@"(?:_workitems/edit/|/workitems/|[?&]workitem=)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkItemUrlRegex();

    public static string Json(JsonNode? node, string fallback = "{}") => node?.ToJsonString() ?? fallback;

    // Aceita o número do card ("123" ou "#123") ou qualquer URL que o contenha
    // (".../_workitems/edit/123", "...?workitem=123", ".../_apis/wit/workItems/123").
    public static int ResolveWorkItemId(string workItemIdOrUrl)
    {
        var value = workItemIdOrUrl.Trim().TrimStart('#');
        if (int.TryParse(value, out var id) && id > 0) return id;

        var match = WorkItemUrlRegex().Match(value);
        if (match.Success && int.TryParse(match.Groups[1].Value, out id)) return id;

        throw new McpException($"ID de work item inválido: '{workItemIdOrUrl}'. Informe o número ou a URL do card.");
    }

    public static List<int> ResolveWorkItemIds(string workItemIdsOrUrls) =>
        [.. workItemIdsOrUrls
            .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ResolveWorkItemId)
            .Distinct()];

    // Cards de Bug não têm o campo Description no Azure DevOps: o conteúdo descritivo vai em Repro Steps.
    public static string DescriptionFieldFor(string? type) =>
        string.Equals(type, "Bug", StringComparison.OrdinalIgnoreCase) ? ReproStepsField : DescriptionField;

    public static string EscapeWiql(string value) => value.Replace("'", "''");

    public static void AddField(JsonArray operations, string field, JsonNode? value) =>
        operations.Add(new JsonObject { ["op"] = "add", ["path"] = $"/fields/{field}", ["value"] = value });

    private static void AddFieldIfPresent(JsonArray operations, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) AddField(operations, field, value);
    }

    public static void AddCustomFields(JsonArray operations, JsonObject? fields)
    {
        if (fields is null) return;
        foreach (var (name, value) in fields)
            AddField(operations, name.StartsWith("/fields/", StringComparison.Ordinal) ? name["/fields/".Length..] : name, value?.DeepClone());
    }

    public static JsonArray BuildCreateOperations(WorkItemSpec spec)
    {
        var operations = new JsonArray();
        AddField(operations, "System.Title", spec.Title);
        AddFieldIfPresent(operations, DescriptionFieldFor(spec.Type), spec.Description);
        AddFieldIfPresent(operations, "System.AssignedTo", spec.AssignedTo);
        AddFieldIfPresent(operations, "System.State", spec.State);
        AddFieldIfPresent(operations, "System.AreaPath", spec.AreaPath);
        AddFieldIfPresent(operations, "System.IterationPath", spec.IterationPath);
        AddFieldIfPresent(operations, "System.Tags", spec.Tags);
        AddCustomFields(operations, spec.Fields);
        return operations;
    }

    public static WorkItemSpec ParseSpec(JsonNode? node, string? defaultType = null)
    {
        if (node is not JsonObject obj)
            throw new McpException("Cada card deve ser um objeto JSON com ao menos 'type' e 'title'.");

        var title = ReadString(obj, "title");
        var type = ReadString(obj, "type") ?? defaultType;
        if (string.IsNullOrWhiteSpace(title))
            throw new McpException("Card sem 'title'.");
        if (string.IsNullOrWhiteSpace(type))
            throw new McpException($"Card '{title}' sem 'type'.");

        return new WorkItemSpec(
            type,
            title,
            ReadString(obj, "description"),
            ReadString(obj, "assigned_to", "assignedTo"),
            ReadString(obj, "state"),
            ReadString(obj, "area_path", "areaPath"),
            ReadString(obj, "iteration_path", "iterationPath"),
            ReadTags(obj["tags"]),
            obj["fields"] as JsonObject);
    }

    private static string? ReadString(JsonObject obj, params string[] names)
    {
        foreach (var name in names)
            if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        return null;
    }

    private static string? ReadTags(JsonNode? node) => node switch
    {
        JsonArray array => string.Join("; ", array.Select(t => t?.ToString()).Where(t => !string.IsNullOrWhiteSpace(t))),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => null
    };

    public static string? GetString(JsonObject? fields, string name) =>
        fields?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var text = ListItemRegex().Replace(html, "- ");
        text = BlockEndRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace("\r", string.Empty).Replace('\u00A0', ' ');
        text = LineSpacesRegex().Replace(text, "\n");
        text = ExtraBlankLinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    // Campos de identidade vêm como objeto ({displayName, uniqueName, ...}); devolve "Nome <email>",
    // o mesmo formato aceito em System.AssignedTo e nas cláusulas WIQL.
    public static string? FormatIdentity(JsonNode? identity)
    {
        if (identity is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (identity is not JsonObject obj) return null;

        var displayName = obj["displayName"]?.GetValue<string>();
        var uniqueName = obj["uniqueName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uniqueName) || uniqueName == displayName) return displayName;
        return string.IsNullOrWhiteSpace(displayName) ? uniqueName : $"{displayName} <{uniqueName}>";
    }

    public static JsonArray SplitTags(string? tags) =>
        new([.. (tags ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => (JsonNode)JsonValue.Create(t))]);

    private static int? IdFromApiUrl(string? url) =>
        url is not null && int.TryParse(url[(url.LastIndexOf('/') + 1)..], out var id) ? id : null;

    public static IEnumerable<int> ExtractRelatedIds(JsonArray relations, string relationType) =>
        relations.OfType<JsonObject>()
            .Where(r => r["rel"]?.GetValue<string>() == relationType)
            .Select(r => IdFromApiUrl(r["url"]?.GetValue<string>()))
            .OfType<int>();

    public static int? ExtractParentId(JsonObject? workItem)
    {
        if (workItem?["fields"]?["System.Parent"] is JsonValue parent && parent.TryGetValue<int>(out var parentId))
            return parentId;

        if (workItem?["relations"] is JsonArray relations)
            foreach (var id in ExtractRelatedIds(relations, McpClient.ParentRelation))
                return id;

        return null;
    }

    public static JsonObject SummarizeListItem(JsonObject? workItem, McpClient client)
    {
        var fields = workItem?["fields"] as JsonObject;
        var id = workItem?["id"]?.GetValue<int>() ?? 0;
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = GetString(fields, "System.WorkItemType"),
            ["title"] = GetString(fields, "System.Title"),
            ["state"] = GetString(fields, "System.State"),
            ["assignedTo"] = FormatIdentity(fields?["System.AssignedTo"]),
            ["parentId"] = ExtractParentId(workItem),
            ["changedDate"] = fields?["System.ChangedDate"]?.DeepClone(),
            ["url"] = workItem?["_links"]?["html"]?["href"]?.GetValue<string>() ?? client.WorkItemWebUrl(id)
        };
    }

    public static JsonObject SummarizeWorkItem(JsonObject? workItem, McpClient client, bool full = false)
    {
        var fields = workItem?["fields"] as JsonObject;
        var relations = workItem?["relations"] as JsonArray;
        var descriptionField = DescriptionFieldFor(GetString(fields, "System.WorkItemType"));
        var descriptionHtml = GetString(fields, descriptionField);

        var summary = SummarizeListItem(workItem, client);
        summary["areaPath"] = GetString(fields, "System.AreaPath");
        summary["iterationPath"] = GetString(fields, "System.IterationPath");
        summary["tags"] = SplitTags(GetString(fields, "System.Tags"));
        summary["createdBy"] = FormatIdentity(fields?["System.CreatedBy"]);
        summary["createdDate"] = fields?["System.CreatedDate"]?.DeepClone();
        if (relations is not null)
            summary["childIds"] = new JsonArray([.. ExtractRelatedIds(relations, McpClient.ChildRelation).Select(id => (JsonNode)id)]);
        summary["descriptionField"] = descriptionField;
        summary["description"] = StripHtml(descriptionHtml);

        if (full)
        {
            summary["descriptionHtml"] = descriptionHtml;
            summary["fields"] = fields?.DeepClone();
            summary["relations"] = relations?.DeepClone();
        }

        return summary;
    }

    public static JsonObject SummarizeCreated(JsonObject? workItem, McpClient client)
    {
        var summary = SummarizeListItem(workItem, client);
        summary.Remove("assignedTo");
        summary.Remove("changedDate");
        return summary;
    }

    public static IEnumerable<JsonObject> OrderByIds(JsonArray workItems, IEnumerable<int> ids)
    {
        var byId = workItems.OfType<JsonObject>()
            .Where(w => w["id"] is not null)
            .ToDictionary(w => w["id"]!.GetValue<int>());
        foreach (var id in ids)
            if (byId.TryGetValue(id, out var workItem))
                yield return workItem;
    }
}
