using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.IO.Abstractions;
using Mono.Cecil;
using Lumafly.Interfaces;

namespace Lumafly.Util;

public class CheckValidityOfAssembly : ICheckValidityOfAssembly
{
    private readonly IFileSystem _fs;
    private readonly ISettings _settings;
    
    public CheckValidityOfAssembly(IFileSystem fs, ISettings settings)
    {
        _fs = fs;
        _settings = settings;
    }
    
    public int? GetAPIVersion(string asmName)
    {
        try
        {
            string asm = Path.Combine(_settings.ManagedFolder, asmName);
            if (!File.Exists(asm)) 
                return null;

            using AssemblyDefinition asmDefinition = AssemblyDefinition.ReadAssembly(asm);

            var modhooks = asmDefinition.MainModule.GetType("Modding.ModHooks");
            if (modhooks is null)  
                return null;

            FieldDefinition? ver = modhooks.Fields.FirstOrDefault(x => x.Name == "_modVersion");
                
            if (ver is null || !ver.IsLiteral) throw new InvalidOperationException("Invalid ModdingAPI file");
            
            return (int) ver.Constant;
        }
        catch (Exception e) 
        {
            Trace.WriteLine(e);
            return null;
        }
    }

    public string? GetGameVersion(string asmName)
    {
        string asm = Path.Combine(_settings.ManagedFolder, asmName);
        if (!_fs.File.Exists(asm))
            return null;

        using Stream stream = _fs.File.OpenRead(asm);
        return ReadGameVersion(stream);
    }

    /// <param name="assembly">A seekable stream containing an Assembly-CSharp build.</param>
    public static string? ReadGameVersion(Stream assembly)
    {
        try
        {
            using AssemblyDefinition asmDefinition = AssemblyDefinition.ReadAssembly(assembly);

            FieldDefinition? ver = asmDefinition.MainModule.GetType("Constants")?
                .Fields.FirstOrDefault(x => x.Name == "GAME_VERSION");

            return ver is { IsLiteral: true } ? ver.Constant as string : null;
        }
        catch (BadImageFormatException e)
        {
            Trace.WriteLine(e);
            return null;
        }
    }

    public bool CheckVanillaFileValidity(string vanillaAssembly, string? gameVersion)
    {
        // check if the file is there, the file doesnt have monomod, and it belongs to the installed game version
        return _fs.File.Exists(Path.Combine(_settings.ManagedFolder, vanillaAssembly)) &&
               GetAPIVersion(vanillaAssembly) == null &&
               GameVersion.Equal(GetGameVersion(vanillaAssembly), gameVersion);
    }
}