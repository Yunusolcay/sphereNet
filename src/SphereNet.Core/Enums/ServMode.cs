namespace SphereNet.Core.Enums;

/// <summary>
/// Server operational mode. Maps to ServMode in Source-X CServer.h.
/// </summary>
public enum ServMode : byte
{
    RestockAll = 0,
    Loading,
    Run,
    Exiting,
    Saving,
    ResyncPause,
    LoadingSaves
}
