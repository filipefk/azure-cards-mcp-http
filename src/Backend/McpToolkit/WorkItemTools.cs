using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;
using static McpToolkit.McpToolsHelpers;

namespace McpToolkit;

[McpServerToolType]
public sealed class WorkItemTools(McpClient azure)
{
    private const string FieldsExample = """{"Microsoft.VSTS.Common.Priority":2,"Microsoft.VSTS.Scheduling.StoryPoints":5}""";

    [McpServerTool(Name = "get_work_item", Title = "Obter card", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Obtém um card (work item) do Azure DevOps pelo ID ou URL. Por padrão retorna um resumo: tipo, título, estado, responsável, área, iteração, tags, pai (parentId), filhos (childIds), URL e a descrição em texto puro (para Bug, o conteúdo de Repro Steps). Use full=true para receber também todos os campos, a descrição em HTML e as relações.")]
    public async Task<string> GetWorkItem(
        [Description("ID ou URL do card")] string work_item_id,
        [Description("Se true, inclui todos os campos, a descrição em HTML e as relações")] bool full = false,
        CancellationToken ct = default)
    {
        var id = ResolveWorkItemId(work_item_id);
        var workItem = await azure.GetWorkItemAsync(id, true, ct) as JsonObject;
        return Json(SummarizeWorkItem(workItem, azure, full));
    }

    [McpServerTool(Name = "get_work_items", Title = "Obter vários cards", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Obtém vários cards de uma vez pelos IDs ou URLs, no mesmo formato resumido de get_work_item. IDs inexistentes são ignorados.")]
    public async Task<string> GetWorkItems(
        [Description("IDs ou URLs dos cards, separados por vírgula (ex: '101,102,105')")] string work_item_ids,
        CancellationToken ct = default)
    {
        var ids = ResolveWorkItemIds(work_item_ids);
        var workItems = await azure.GetWorkItemsAsync(ids, true, ct);
        return Json(new JsonArray([.. OrderByIds(workItems, ids).Select(w => (JsonNode)SummarizeWorkItem(w, azure))]), "[]");
    }

    [McpServerTool(Name = "create_work_item", Title = "Criar card", Destructive = false, OpenWorld = false),
     Description("Cria um card (work item) no projeto do Azure DevOps. A descrição é gravada automaticamente no campo certo para o tipo: Repro Steps (Microsoft.VSTS.TCM.ReproSteps) para Bug, Description para os demais. Informe parent_id para criar o card já como filho de um card existente. Retorna id, tipo, título, estado, parentId e URL do card criado.")]
    public async Task<string> CreateWorkItem(
        [Description("Tipo do work item, ex: 'User Story', 'Bug', 'Task' (veja list_work_item_types)")] string type,
        [Description("Título do card")] string title,
        [Description("Descrição em HTML (o Azure DevOps renderiza HTML nos campos de texto)")] string? description = null,
        [Description("ID ou URL de um card existente para vincular como pai (opcional)")] string? parent_id = null,
        [Description("Responsável: e-mail ou 'Nome <email>' (opcional)")] string? assigned_to = null,
        [Description("Estado inicial (opcional; se omitido usa o estado inicial do processo — veja list_work_item_states)")] string? state = null,
        [Description("Area Path (opcional)")] string? area_path = null,
        [Description("Iteration Path / sprint (opcional)")] string? iteration_path = null,
        [Description("Tags separadas por ponto e vírgula, ex: 'backend; urgente' (opcional)")] string? tags = null,
        [Description("Outros campos pelo reference name (opcional), ex: " + FieldsExample)] JsonObject? fields = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(type)) throw new McpException("Informe o tipo do work item.");
        if (string.IsNullOrWhiteSpace(title)) throw new McpException("Informe o título do card.");

        var spec = new WorkItemSpec(type, title, description, assigned_to, state, area_path, iteration_path, tags, fields);
        int? parentId = string.IsNullOrWhiteSpace(parent_id) ? null : ResolveWorkItemId(parent_id);
        return Json(await CreateAsync(spec, parentId, ct));
    }

    [McpServerTool(Name = "create_work_item_tree", Title = "Criar card com filhos", Destructive = false, OpenWorld = false),
     Description("Cria um card pai e, em seguida, cada card filho já vinculado a ele (ex: uma User Story com Tasks filhas). Cada card é um objeto com 'type' e 'title' e, opcionalmente, 'description' (HTML), 'assigned_to', 'state', 'area_path', 'iteration_path', 'tags' e 'fields'. Nos filhos, 'type' é 'Task' se omitido. Se o pai falhar, nada é criado; se um filho falhar, os demais continuam e a falha é listada em 'errors'.")]
    public async Task<string> CreateWorkItemTree(
        [Description("Card pai, ex: {\"type\":\"User Story\",\"title\":\"...\",\"description\":\"<p>...</p>\"}")] JsonObject parent,
        [Description("Cards filhos, no mesmo formato do pai, ex: [{\"title\":\"...\"},{\"type\":\"Task\",\"title\":\"...\"}]")] JsonArray children,
        [Description("ID ou URL de um card existente ao qual o card pai será vinculado (opcional)")] string? parent_id = null,
        CancellationToken ct = default)
    {
        var parentSpec = ParseSpec(parent);
        int? grandparentId = string.IsNullOrWhiteSpace(parent_id) ? null : ResolveWorkItemId(parent_id);
        var createdParent = await CreateAsync(parentSpec, grandparentId, ct);
        var createdParentId = createdParent["id"]!.GetValue<int>();

        var createdChildren = new JsonArray();
        var errors = new JsonArray();
        foreach (var child in children)
        {
            var childTitle = child?["title"]?.ToString();
            try
            {
                createdChildren.Add(await CreateAsync(ParseSpec(child, "Task"), createdParentId, ct));
            }
            catch (Exception ex) when (ex is McpException or HttpRequestException)
            {
                errors.Add(new JsonObject { ["title"] = childTitle, ["message"] = ex.Message });
            }
        }

        return Json(new JsonObject
        {
            ["parent"] = createdParent,
            ["children"] = createdChildren,
            ["errors"] = errors
        });
    }

    [McpServerTool(Name = "update_work_item", Title = "Atualizar card", Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Atualiza campos de um card existente. Só os parâmetros informados são alterados (omitido = não altera; string vazia = limpa o campo). A descrição vai no campo certo para o tipo (Repro Steps para Bug). 'tags' substitui todas as tags do card. Para mudar de coluna/estado use 'state' (veja list_work_item_states). Retorna o resumo do card atualizado.")]
    public async Task<string> UpdateWorkItem(
        [Description("ID ou URL do card")] string work_item_id,
        [Description("Novo título")] string? title = null,
        [Description("Nova descrição em HTML")] string? description = null,
        [Description("Novo estado, ex: 'Active', 'Resolved', 'Closed', 'Doing', 'Done'")] string? state = null,
        [Description("Novo responsável: e-mail ou 'Nome <email>'; string vazia remove o responsável")] string? assigned_to = null,
        [Description("Novo Area Path")] string? area_path = null,
        [Description("Novo Iteration Path / sprint")] string? iteration_path = null,
        [Description("Tags separadas por ponto e vírgula — substitui todas as tags atuais")] string? tags = null,
        [Description("Outros campos pelo reference name, ex: " + FieldsExample)] JsonObject? fields = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkItemId(work_item_id);
        var operations = new JsonArray();

        if (title is not null) AddField(operations, "System.Title", title);
        if (description is not null)
        {
            var current = await azure.GetWorkItemAsync(id, false, ct) as JsonObject;
            var type = GetString(current?["fields"] as JsonObject, "System.WorkItemType");
            AddField(operations, DescriptionFieldFor(type), description);
        }
        if (state is not null) AddField(operations, "System.State", state);
        if (assigned_to is not null) AddField(operations, "System.AssignedTo", assigned_to);
        if (area_path is not null) AddField(operations, "System.AreaPath", area_path);
        if (iteration_path is not null) AddField(operations, "System.IterationPath", iteration_path);
        if (tags is not null) AddField(operations, "System.Tags", tags);
        AddCustomFields(operations, fields);

        if (operations.Count == 0)
            throw new McpException("Nenhum campo para atualizar foi informado.");

        var updated = await azure.UpdateWorkItemAsync(id, operations, ct) as JsonObject;
        return Json(SummarizeWorkItem(updated, azure));
    }

    [McpServerTool(Name = "set_work_item_parent", Title = "Definir card pai", Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Vincula um card a um card pai, trocando o pai atual se houver. Sem parent_id, remove o vínculo com o pai. Retorna o resumo do card.")]
    public async Task<string> SetWorkItemParent(
        [Description("ID ou URL do card filho")] string work_item_id,
        [Description("ID ou URL do novo card pai; omita para remover o pai atual")] string? parent_id = null,
        CancellationToken ct = default)
    {
        var id = ResolveWorkItemId(work_item_id);
        int? parentId = string.IsNullOrWhiteSpace(parent_id) ? null : ResolveWorkItemId(parent_id);
        if (parentId == id)
            throw new McpException("Um card não pode ser pai dele mesmo.");

        var updated = await azure.SetParentAsync(id, parentId, ct) as JsonObject;
        return Json(SummarizeWorkItem(updated, azure));
    }

    private async Task<JsonObject> CreateAsync(WorkItemSpec spec, int? parentId, CancellationToken ct)
    {
        var operations = BuildCreateOperations(spec);
        if (parentId is int id)
            operations.Add(azure.BuildParentRelationOperation(id));

        var created = await azure.CreateWorkItemAsync(spec.Type, operations, ct) as JsonObject;
        return SummarizeCreated(created, azure);
    }
}
