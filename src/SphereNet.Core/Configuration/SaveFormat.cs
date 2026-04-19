namespace SphereNet.Core.Configuration;

/// <summary>
/// On-disk encoding for world/account saves. Chosen via sphere.ini SaveFormat.
/// Loader auto-detects from file extension, so changing this only affects new
/// saves — existing snapshots keep loading.
/// </summary>
public enum SaveFormat
{
    /// <summary>Classic Sphere <c>.scp</c> text, human-readable, largest on disk.</summary>
    Text = 0,
    /// <summary><c>.scp.gz</c> — same text, GZip-wrapped. ~85% smaller, identical CPU on save.</summary>
    TextGz = 1,
    /// <summary><c>.sbin</c> — length-prefixed binary envelope around the same
    /// (key, value) pairs as the text format. No per-field schema.</summary>
    Binary = 2,
    /// <summary><c>.sbin.gz</c> — binary + gzip. Smallest on disk.</summary>
    BinaryGz = 3,
}
