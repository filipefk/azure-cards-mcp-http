using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace McpToolkit.Shell;

[McpServerToolType]
public class ShellTools(IOptions<ShellOptions> options, ILogger<ShellTools> logger, ShellCommandRunner runner)
{
    [McpServerTool(Name = "execute_shell_command"), Description(
        "Executa um comando no shell do sistema operacional onde este MCP está rodando " +
        "(PowerShell/cmd no Windows, bash/sh no Linux e macOS). Use get_shell_info antes " +
        "para saber qual shell está disponível e se uma senha é exigida.")]
    public async Task<ShellCommandResult> ExecuteShellCommand(
        [Description("Comando a ser executado no shell.")] string command,
        [Description("Shell a ser usado. Se omitido ou 'Auto', o shell padrão do sistema operacional é escolhido automaticamente.")] ShellKind shell = ShellKind.Auto,
        [Description("Diretório de trabalho para executar o comando. Se omitido, usa o diretório padrão configurado no servidor.")] string? workingDirectory = null,
        [Description("Tempo máximo em segundos para o comando executar antes de ser cancelado. Sujeito a um teto configurado no servidor.")] int? timeoutSeconds = null,
        [Description("Senha exigida apenas se o servidor estiver configurado com uma. Consulte get_shell_info para saber se é necessária.")] string? password = null)
    {
        EnsureAuthorized(password);

        logger.LogInformation(
            "Executando comando de shell (shell solicitado: {RequestedShell}, workingDirectory: {WorkingDirectory})",
            shell, workingDirectory ?? options.Value.WorkingDirectory);

        return await runner.RunAsync(command, shell, workingDirectory, timeoutSeconds);
    }

    [McpServerTool(Name = "get_shell_info"), Description(
        "Retorna informações sobre o shell disponível neste servidor: sistema operacional, " +
        "shell padrão, shells disponíveis, diretório de trabalho e limites configurados. " +
        "Não exige senha.")]
    public ShellInfoResult GetShellInfo()
    {
        var settings = options.Value;

        var operatingSystem = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux"
            : "Desconhecido";

        var availableShells = ShellResolver.GetAvailableShells()
            .Select(s => new ShellAvailabilityInfo(s.Kind.ToString(), s.Available))
            .ToList();

        return new ShellInfoResult(
            OperatingSystem: operatingSystem,
            DefaultShell: ShellResolver.ResolveDefaultKind().ToString(),
            AvailableShells: availableShells,
            WorkingDirectory: string.IsNullOrWhiteSpace(settings.WorkingDirectory)
                ? Environment.CurrentDirectory
                : settings.WorkingDirectory,
            TimeoutSeconds: settings.TimeoutSeconds,
            MaxOutputChars: settings.MaxOutputChars,
            RequiresPassword: !string.IsNullOrEmpty(settings.Password));
    }

    private void EnsureAuthorized(string? password)
    {
        var configuredPassword = options.Value.Password;
        if (string.IsNullOrEmpty(configuredPassword))
            return;

        var providedBytes = Encoding.UTF8.GetBytes(password ?? "");
        var configuredBytes = Encoding.UTF8.GetBytes(configuredPassword);

        var isValid = providedBytes.Length == configuredBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);

        if (!isValid)
            throw new McpException("Senha obrigatória ou incorreta para executar comandos de shell.");
    }
}
