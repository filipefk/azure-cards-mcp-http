using Microsoft.Extensions.Options;
using ModelContextProtocol;
using System.Diagnostics;
using System.Text;

namespace McpToolkit.Shell;

public class ShellCommandRunner(IOptions<ShellOptions> options)
{
    public async Task<ShellCommandResult> RunAsync(
        string command,
        ShellKind requestedShell,
        string? workingDirectoryOverride,
        int? timeoutSecondsOverride,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(command))
            throw new McpException("O parâmetro 'command' não pode ser vazio.");

        EnsureCommandAllowed(command, settings);

        var workingDirectory = ResolveWorkingDirectory(workingDirectoryOverride, settings);
        var effectiveTimeoutSeconds = ResolveTimeoutSeconds(timeoutSecondsOverride, settings);

        var (fileName, arguments, effectiveShell) = ShellResolver.Resolve(requestedShell, command);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        var timedOut = false;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.AppendLine(e.Data); };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new McpException($"Falha ao iniciar o shell '{fileName}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }

        stopwatch.Stop();
        process.WaitForExit();

        var (truncatedOut, outWasTruncated) = Truncate(stdOut.ToString(), settings.MaxOutputChars);
        var (truncatedErr, errWasTruncated) = Truncate(stdErr.ToString(), settings.MaxOutputChars);

        return new ShellCommandResult(
            ExitCode: timedOut ? -1 : process.ExitCode,
            StdOut: truncatedOut,
            StdErr: truncatedErr,
            TimedOut: timedOut,
            Truncated: outWasTruncated || errWasTruncated,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Shell: effectiveShell.ToString(),
            WorkingDirectory: workingDirectory);
    }

    private static void EnsureCommandAllowed(string command, ShellOptions settings)
    {
        var firstToken = command.TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        var commandName = firstToken is null ? "" : Path.GetFileNameWithoutExtension(firstToken);

        if (settings.BlockedCommands.Length > 0 &&
            settings.BlockedCommands.Any(blocked => string.Equals(blocked, commandName, StringComparison.OrdinalIgnoreCase)))
            throw new McpException($"O comando '{commandName}' está bloqueado pela configuração deste servidor.");

        if (settings.AllowedCommands.Length > 0 &&
            !settings.AllowedCommands.Any(allowed => string.Equals(allowed, commandName, StringComparison.OrdinalIgnoreCase)))
            throw new McpException($"O comando '{commandName}' não está na lista de comandos permitidos deste servidor.");
    }

    private static string ResolveWorkingDirectory(string? workingDirectoryOverride, ShellOptions settings)
    {
        var workingDirectory = !string.IsNullOrWhiteSpace(workingDirectoryOverride)
            ? workingDirectoryOverride
            : settings.WorkingDirectory;

        if (string.IsNullOrWhiteSpace(workingDirectory))
            return Environment.CurrentDirectory;

        if (!Directory.Exists(workingDirectory))
            throw new McpException($"O diretório de trabalho '{workingDirectory}' não existe.");

        return workingDirectory;
    }

    private static int ResolveTimeoutSeconds(int? timeoutSecondsOverride, ShellOptions settings)
    {
        var ceiling = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 60;

        if (timeoutSecondsOverride is null or <= 0)
            return ceiling;

        return Math.Min(timeoutSecondsOverride.Value, ceiling);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Processo pode já ter saído entre a checagem e o kill.
        }
    }

    private static (string Text, bool Truncated) Truncate(string text, int maxChars)
    {
        if (maxChars <= 0 || text.Length <= maxChars)
            return (text, false);

        return (text[..maxChars], true);
    }
}
