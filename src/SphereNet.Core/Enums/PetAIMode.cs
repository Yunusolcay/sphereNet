namespace SphereNet.Core.Enums;

/// <summary>
/// Pet AI behavior modes. Controlled by player speech commands.
/// </summary>
public enum PetAIMode : byte
{
    Follow = 0,
    Guard = 1,
    Attack = 2,
    Stay = 3,
    Stop = 4,
    Come = 5,
}
