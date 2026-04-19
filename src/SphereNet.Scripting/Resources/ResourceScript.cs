using SphereNet.Scripting.Parsing;

namespace SphereNet.Scripting.Resources;

/// <summary>
/// Managed script file reference with open count tracking.
/// Maps to CResourceScript in Source-X.
/// Tracks file size/modification time for ReSync detection.
/// </summary>
public sealed class ResourceScript : IDisposable
{
    private ScriptFile? _file;
    private int _openCount;

    public string FilePath { get; }
    public long FileSize { get; private set; }
    public DateTime LastModified { get; private set; }
    public bool IsOpen => _file != null && _file.IsOpen;

    public ResourceScript(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
    }

    public ScriptFile Open()
    {
        if (_file == null || !_file.IsOpen)
        {
            _file = new ScriptFile { UseCache = true };
            if (!_file.Open(FilePath))
                throw new FileNotFoundException($"Script file not found: {FilePath}");

            var info = new FileInfo(FilePath);
            FileSize = info.Length;
            LastModified = info.LastWriteTimeUtc;
        }

        _openCount++;
        return _file;
    }

    public void Close()
    {
        _openCount--;
        if (_openCount <= 0)
        {
            _file?.Close();
            _file = null;
            _openCount = 0;
        }
    }

    /// <summary>
    /// Check if the file has been modified since last open.
    /// </summary>
    public bool NeedsReSync()
    {
        if (!File.Exists(FilePath)) return false;
        var info = new FileInfo(FilePath);
        return info.Length != FileSize || info.LastWriteTimeUtc != LastModified;
    }

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
        _openCount = 0;
    }
}
