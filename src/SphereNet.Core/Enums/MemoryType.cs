namespace SphereNet.Core.Enums;

[Flags]
public enum MemoryType : ushort
{
    None          = 0,
    SawCrime      = 0x0001,
    IPet          = 0x0002,
    Fight         = 0x0004,
    IAggressor    = 0x0008,
    HarmedBy      = 0x0010,
    IrritatedBy   = 0x0020,
    Speak         = 0x0040,
    Aggreived     = 0x0080,
    Guard         = 0x0100,
    Guild         = 0x0400,
    Town          = 0x0800,
    Friend        = 0x4000,
}
