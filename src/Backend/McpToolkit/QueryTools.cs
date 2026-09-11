using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;
using static McpToolkit.McpToolsHelpers;

namespace McpToolkit;

[McpServerToolType]
public sealed class QueryTools(McpClient azure)
{
    // Estados finais dos processos padrão (Agile, Scrum, Basic, CMMI) — o Azure DevOps não expõe
    // a categoria do estado em WIQL, então o filtro de "abertos" é feito pelos nomes.
    private static readonly string[] ClosedStates = ["Closed", "Done", "Removed", "Completed"];

    [McpServerTool(Name = "search_work_items", Title = "Buscar cards", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Busca cards do projeto combinando filtros opcionais (todos com AND). Por padrão exclui cards nos estados Closed, Done, Removed e Completed e ordena pelos alterados mais recentemente. Retorna um resumo por card (id, tipo, título, estado, responsável, parentId, changedDate, url), 'truncated' se houver mais resultados que 'top', e a WIQL usada (útil para refinar com query_wiql).")]
    public async Task<string> SearchWorkItems(
        [Description("Tipo do work item, ex: 'Bug', 'Task', 'User Story'")] string? type = null,
        [Description("Estado exato, ex: 'Active', 'New', 'Doing'. Quando informado, include_closed é ignorado")] string? state = null,
        [Description("Responsável: e-mail, 'Nome <email>', '@me' para o dono da chave de acesso, ou string vazia para cards sem responsável")] string? assigned_to = null,
        [Description("Texto contido no título")] string? text = null,
        [Description("Area Path — inclui as subáreas")] string? area_path = null,
        [Description("Iteration Path / sprint — inclui as sub-iterações")] string? iteration_path = null,
        [Description("Tag")] string? tag = null,
        [Description("Somente filhos diretos deste card (ID ou URL)")] string? parent_id = null,
        [Description("Se true, inclui cards em estados finais (Closed, Done, Removed, Completed)")] bool include_closed = false,
        [Description("Máximo de cards retornados (padrão 50, máximo 200)")] int top = 50,
        CancellationToken ct = default)
    {
        var conditions = new List<string> { "[System.TeamProject] = @project" };

        if (!string.IsNullOrWhiteSpace(type))
            conditions.Add($"[System.WorkItemType] = '{EscapeWiql(type.Trim())}'");

        if (!string.IsNullOrWhiteSpace(state))
            conditions.Add($"[System.State] = '{EscapeWiql(state.Trim())}'");
        else if (!include_closed)
            conditions.Add($"[System.State] NOT IN ({string.Join(", ", ClosedStates.Select(s => $"'{s}'"))})");

        if (assigned_to is not null)
        {
            var assignee = assigned_to.Trim();
            conditions.Add(assignee.Equals("@me", StringComparison.OrdinalIgnoreCase)
                ? "[System.AssignedTo] = @me"
                : $"[System.AssignedTo] = '{EscapeWiql(assignee)}'");
        }

        if (!string.IsNullOrWhiteSpace(text))
            conditions.Add($"[System.Title] CONTAINS '{EscapeWiql(text.Trim())}'");
        if (!string.IsNullOrWhiteSpace(area_path))
            conditions.Add($"[System.AreaPath] UNDER '{EscapeWiql(area_path.Trim())}'");
        if (!string.IsNullOrWhiteSpace(iteration_path))
            conditions.Add($"[System.IterationPath] UNDER '{EscapeWiql(iteration_path.Trim())}'");
        if (!string.IsNullOrWhiteSpace(tag))
            conditions.Add($"[System.Tags] CONTAINS '{EscapeWiql(tag.Trim())}'");
        if (!string.IsNullOrWhiteSpace(parent_id))
            conditions.Add($"[System.Parent] = {ResolveWorkItemId(parent_id)}");

        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE {string.Join(" AND ", conditions)} ORDER BY [System.ChangedDate] DESC";
        var limit = Math.Clamp(top, 1, MaxTop);

        // Pede um a mais que o limite só para saber se há mais resultados (truncated).
        var result = await azure.QueryByWiqlAsync(wiql, limit + 1, ct) as JsonObject;
        var ids = ExtractIds(result?["workItems"] as JsonArray);

        var response = await BuildResultAsync(ids, limit, ct);
        response["query"] = wiql;
        return Json(response);
    }

    [McpServerTool(Name = "query_wiql", Title = "Executar WIQL", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Executa uma consulta WIQL (Work Item Query Language) livre no projeto e devolve o resumo de cada card encontrado (a WIQL só retorna IDs; os detalhes são buscados em lote). Use a macro @project para restringir ao projeto atual e @me para o usuário da chave. Consultas de links (FROM WorkItemLinks, ex: árvore pai/filho) retornam também 'relations' (rel, sourceId, targetId). Ex: SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] = 'Bug' ORDER BY [System.CreatedDate] DESC")]
    public async Task<string> QueryWiql(
        [Description("Texto da consulta WIQL")] string query,
        [Description("Máximo de cards retornados (padrão 50, máximo 200)")] int top = 50,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("Informe a consulta WIQL.");

        var limit = Math.Clamp(top, 1, MaxTop);
        var result = await azure.QueryByWiqlAsync(query, limit + 1, ct) as JsonObject;

        List<int> ids;
        JsonArray? relations = null;
        if (result?["workItemRelations"] is JsonArray links)
        {
            relations = new JsonArray();
            ids = [];
            foreach (var link in links.OfType<JsonObject>())
            {
                var sourceId = link["source"]?["id"]?.GetValue<int>();
                var targetId = link["target"]?["id"]?.GetValue<int>();
                relations.Add(new JsonObject
                {
                    ["rel"] = link["rel"]?.DeepClone(),
                    ["sourceId"] = sourceId,
                    ["targetId"] = targetId
                });
                if (sourceId is int source) ids.Add(source);
                if (targetId is int target) ids.Add(target);
            }
            ids = [.. ids.Distinct()];
        }
        else
        {
            ids = ExtractIds(result?["workItems"] as JsonArray);
        }

        var response = await BuildResultAsync(ids, limit, ct);
        response["queryType"] = result?["queryType"]?.DeepClone();
        if (relations is not null)
            response["relations"] = relations;
        return Json(response);
    }

    [McpServerTool(Name = "list_work_item_types", Title = "Listar tipos de card", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Lista os tipos de work item disponíveis no projeto (ex: Epic, Feature, User Story, Task, Bug), com o nome a ser usado em create_work_item.")]
    public async Task<string> ListWorkItemTypes(
        [Description("Se true, inclui também os tipos desabilitados")] bool include_disabled = false,
        CancellationToken ct = default)
    {
        var result = await azure.ListWorkItemTypesAsync(ct);
        var types = (result?["value"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(t => include_disabled || t["isDisabled"]?.GetValue<bool>() != true)
            .Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t["name"]?.DeepClone(),
                ["referenceName"] = t["referenceName"]?.DeepClone(),
                ["description"] = t["description"]?.DeepClone(),
                ["isDisabled"] = t["isDisabled"]?.DeepClone()
            });
        return Json(new JsonArray([.. types]), "[]");
    }

    [McpServerTool(Name = "list_work_item_states", Title = "Listar estados do tipo", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Lista os estados possíveis de um tipo de work item (ex: New, Active, Resolved, Closed), com a categoria de cada um (Proposed, InProgress, Resolved, Completed, Removed). Use para saber os valores válidos de 'state' em create_work_item/update_work_item.")]
    public async Task<string> ListWorkItemStates(
        [Description("Tipo do work item, ex: 'Bug', 'Task', 'User Story'")] string type,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new McpException("Informe o tipo do work item.");

        var result = await azure.ListWorkItemTypeStatesAsync(type.Trim(), ct);
        var states = (result?["value"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s["name"]?.DeepClone(),
                ["category"] = s["category"]?.DeepClone()
            });
        return Json(new JsonArray([.. states]), "[]");
    }

    private static List<int> ExtractIds(JsonArray? workItems) =>
        [.. (workItems ?? []).OfType<JsonObject>()
            .Select(w => w["id"]?.GetValue<int>() ?? 0)
            .Where(id => id > 0)
            .Distinct()];

    private async Task<JsonObject> BuildResultAsync(List<int> ids, int limit, CancellationToken ct)
    {
        var truncated = ids.Count > limit;
        var page = ids.Take(limit).ToList();
        var workItems = page.Count == 0 ? new JsonArray() : await azure.GetWorkItemsAsync(page, false, ct);

        return new JsonObject
        {
            ["count"] = page.Count,
            ["truncated"] = truncated,
            ["items"] = new JsonArray([.. OrderByIds(workItems, page).Select(w => (JsonNode)SummarizeListItem(w, azure))])
        };
    }
}
