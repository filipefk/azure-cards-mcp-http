namespace McpToolkit.Shell;

public record ShellCommandResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    bool Truncated,
    long DurationMs,
    string Shell,
    string WorkingDirectory);
