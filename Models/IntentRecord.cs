namespace RAM.Models;

public sealed class IntentRecord
{
    public string Title { get; set; } = "";
    public string Objective { get; set; } = "";
    public string Notes { get; set; } = "";
    public string LastUpdatedUtc { get; set; } = "";
}