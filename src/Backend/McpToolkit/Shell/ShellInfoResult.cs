namespace McpToolkit.Shell;

public record ShellAvailabilityInfo(string Shell, bool Available);

public record ShellInfoResult(
    string OperatingSystem,
    string DefaultShell,
    IReadOnlyList<ShellAvailabilityInfo> AvailableShells,
    string WorkingDirectory,
    int TimeoutSeconds,
    int MaxOutputChars,
    bool RequiresPassword);
