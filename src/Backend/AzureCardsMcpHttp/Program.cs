using AzureCardsMcpHttp;
using AzureCardsMcpHttp.Endpoints;
using AzureCardsMcpHttp.Middleware;
using McpToolkit;
using McpToolkit.Shell;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var strLogLevel = configuration.GetValue<string>("Logging:LogLevel:Default");
var enumLogEventLevel = Enum.TryParse<LogEventLevel>(strLogLevel, true, out var parsedLogEventLevel)
    ? parsedLogEventLevel
    : LogEventLevel.Information;
builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration
        .MinimumLevel.Is(enumLogEventLevel)
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .WriteTo.Console(enumLogEventLevel)
        .WriteTo.File("logs/log.txt",
            enumLogEventLevel,
            rollingInterval: RollingInterval.Day));

builder.Services.Configure<McpOptions>(configuration.GetSection("Azure"));
// Compatibilidade com a skill azure-card: sem Azure:Url/Azure:ApiKey configurados,
// usa as mesmas variáveis de ambiente que ela (AZURE_URL_BOARD / AZURE_USER_API_KEY).
builder.Services.PostConfigure<McpOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.Url))
        options.Url = configuration["AZURE_URL_BOARD"] ?? string.Empty;
    if (string.IsNullOrWhiteSpace(options.ApiKey))
        options.ApiKey = configuration["AZURE_USER_API_KEY"] ?? string.Empty;
});
// Chaves MCP autorizadas (primeiro valor do header x-api-key) — lista vazia rejeita todas as chamadas a /mcp.
builder.Services.Configure<McpAuthOptions>(configuration.GetSection("McpAuth"));
builder.Services.Configure<ShellOptions>(configuration.GetSection("Shell"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IRequestContext, HttpRequestContext>();

// Azure:IgnoreSslErrors permite ignorar erros de validação do certificado TLS (ex: Azure DevOps Server
// com certificado autoassinado) — desligado por padrão.
var ignoreSslErrors = configuration.GetValue("Azure:IgnoreSslErrors", false);
builder.Services.AddHttpClient<McpClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = ignoreSslErrors
            ? (_, _, _, _) => true
            : null
    });
builder.Services.AddSingleton<ShellCommandRunner>();

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithHttpTransport();

// Cada família de tools pode ser desligada por configuração (padrão: ligada) — quem só lê/cria cards
// não paga pela superfície (schema/descrições) das demais.
if (configuration.GetValue("Tools:WorkItems:Enabled", true))
    mcpBuilder.WithTools<WorkItemTools>();
if (configuration.GetValue("Tools:Queries:Enabled", true))
    mcpBuilder.WithTools<QueryTools>();
if (configuration.GetValue("Tools:Comments:Enabled", true))
    mcpBuilder.WithTools<CommentTools>();

// Desligado por padrão: defina "Shell:Enabled": true para expor as tools de execução de comandos.
if (configuration.GetValue("Shell:Enabled", false))
    mcpBuilder.WithTools<ShellTools>();

var app = builder.Build();

if (!configuration.GetSection("McpAuth:ApiKeys").GetChildren().Any(k => !string.IsNullOrEmpty(k.Value)))
    app.Logger.LogWarning("Nenhuma chave em McpAuth:ApiKeys — todas as chamadas a /mcp e /api-key serão rejeitadas (401).");

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseSerilogRequestLogging();

// Página da raiz (wwwroot/index.html) para gerar/decodificar a API Key pelos endpoints /api-key.
// É pública: a chave MCP é digitada nela e vai no header, os endpoints continuam protegidos.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp"),
    appBuilder => appBuilder
        .UseMiddleware<McpTrafficLoggingMiddleware>()
        .UseMiddleware<McpApiKeyMiddleware>(McpKeyFormat.Encoded));

// Os endpoints de API Key também exigem o x-api-key, mas com a chave MCP em texto puro: a key
// gerada é o que eles produzem/desfazem. Sem o middleware de tráfego, para o PAT do corpo do
// generate não ir para o log.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api-key"),
    appBuilder => appBuilder.UseMiddleware<McpApiKeyMiddleware>(McpKeyFormat.Raw));

app.MapApiKeyEndpoints();
app.MapMcp("/mcp");

app.Run();
