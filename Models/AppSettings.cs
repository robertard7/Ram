namespace RAM.Models;

public sealed class AppSettings
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b-instruct";
    public string WorkspaceRoot { get; set; } = "";
}