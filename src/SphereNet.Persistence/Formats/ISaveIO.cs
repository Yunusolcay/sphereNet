namespace SphereNet.Persistence.Formats;

/// <summary>
/// Write side of the save/load abstraction. Both the classic Sphere
/// <c>.scp</c> text format and the binary tag-stream format implement this —
/// WorldSaver is oblivious to which one is in use.
/// </summary>
public interface ISaveWriter : IDisposable
{
    /// <summary>Begin one record (Source-X section marker). The text writer
    /// emits <c>[SECTION]</c>; the binary writer emits a framed entry header.</summary>
    void BeginRecord(string section);

    /// <summary>Write one property inside the current record. Values are
    /// UTF-8 in both formats — no type information is carried because the
    /// loader re-interprets them via the existing per-field parsers.</summary>
    void WriteProperty(string key, string value);

    /// <summary>Close the current record (text: <c>[EOF]</c>, binary: frame end).</summary>
    void EndRecord();

    /// <summary>Optional file-level comment. No-op in binary.</summary>
    void WriteHeaderComment(string line);

    /// <summary>Flush buffered writes to the underlying stream.</summary>
    void Flush();

    /// <summary>Approximate byte count written through this writer. Tracked
    /// at the writer layer (not the file stream) so it stays accurate even
    /// when an inner StreamWriter / GZipStream buffer delays disk writes.
    /// Used by size-based shard rolling.</summary>
    long WrittenBytes { get; }
}

/// <summary>
/// Read side. Walks records in file order and yields (key, value) pairs
/// per record. Streaming API to keep memory flat on million-entity loads.
/// </summary>
public interface ISaveReader : IDisposable
{
    /// <summary>Advance to the next record. <paramref name="section"/> is set
    /// to the section name (e.g. "WORLDITEM", "WORLDCHAR"). Returns false
    /// when the file is exhausted.</summary>
    bool NextRecord(out string section);

    /// <summary>Read the next property of the current record. Returns false
    /// when the record has no more properties (caller should loop NextRecord).</summary>
    bool NextProperty(out string key, out string value);
}
