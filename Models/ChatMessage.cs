namespace RAM.Models;

public sealed class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string Timestamp { get; set; } = "";
}