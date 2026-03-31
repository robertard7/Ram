namespace RAM.Models;

public sealed class WorkspaceRetrievalChunkRecord
{
    public string ChunkKey { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string WorkspacePointId { get; set; } = "";
    public string SnapshotId { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public List<string> SolutionPaths { get; set; } = [];
    public string FileKind { get; set; } = "";
    public string ChunkType { get; set; } = "";
    public string Role { get; set; } = "";
    public string LanguageHint { get; set; } = "";
    public List<string> PatternTags { get; set; } = [];
    public string ContentSha256 { get; set; } = "";
    public long ByteCount { get; set; }
    public int LineCount { get; set; }
    public int ChunkOrder { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public List<WorkspaceRetrievalEvidenceRecord> Evidence { get; set; } = [];
}
