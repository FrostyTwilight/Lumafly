namespace Lumafly.Interfaces;

public interface ICheckValidityOfAssembly
{
    public int? GetAPIVersion(string asmName);
    /// <returns>Constants.GAME_VERSION of the assembly, or null if it is missing or unreadable.</returns>
    public string? GetGameVersion(string asmName);
    public bool CheckVanillaFileValidity(string vanillaAssembly, string? gameVersion);
}