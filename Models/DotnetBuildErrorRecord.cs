namespace RAM.Models;

public sealed class DotnetBuildErrorRecord
{
    public string FilePath { get; set; } = "";
    public string RawPath { get; set; } = "";
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string Severity { get; set; } = "";
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public bool InsideWorkspace { get; set; }
    public int Order { get; set; }
}
