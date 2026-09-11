namespace McpToolkit;

public sealed class McpOptions
{
    public string Url { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = "7.1";
    public string CommentsApiVersion { get; set; } = "7.1-preview.4";
    public bool IgnoreSslErrors { get; set; }
}
