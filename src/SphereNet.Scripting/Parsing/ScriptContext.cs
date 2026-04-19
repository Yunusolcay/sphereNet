namespace SphereNet.Scripting.Parsing;

/// <summary>
/// Error tracking context for script files. Maps to CScriptFileContext / CScriptLineContext in Source-X.
/// </summary>
public sealed class ScriptContext
{
    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public long FileOffset { get; set; }

    public ScriptContext Snapshot() => new()
    {
        FilePath = FilePath,
        LineNumber = LineNumber,
        FileOffset = FileOffset
    };

    public override string ToString() => $"{FilePath}({LineNumber})";
}
