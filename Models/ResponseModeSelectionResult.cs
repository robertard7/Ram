namespace RAM.Models;

public sealed class ResponseModeSelectionResult
{
    public ResponseMode Mode { get; set; } = ResponseMode.None;
    public string Reason { get; set; } = "";
}
