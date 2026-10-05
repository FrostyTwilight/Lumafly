using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Lumafly.Interfaces;
using Lumafly.Models;
using Lumafly.Util;

namespace Lumafly.Services
{
    public class ModDatabase : IModDatabase
    {
        public const string LINKS_BASE = "https://raw.githubusercontent.com/hk-modding/modlinks/main";

        private const string FALLBACK_LINKS_BASE = "https://cdn.jsdelivr.net/gh/hk-modding/modlinks@latest";

        // { "<game version>" | "default": "<modlinks base>" }, lets older game versions keep a pinned modlinks
        private const string LinksBaseMapUri = "https://raw.githubusercontent.com/TheMulhima/Lumafly/static-resources/ModLinks.json";

        private const string VanillaApiRepo = "https://raw.githubusercontent.com/TheMulhima/Lumafly/static-resources/AssemblyLinks.json";

        internal const int TIMEOUT = 30_000;

        public (string Url, int Version, string SHA256) Api { get; }

        public List<ModItem> Items => _items;

        private readonly List<ModItem> _items = new();
        private readonly List<string> _itemNames = new();

        private ModDatabase(IModSource mods, 
            IGlobalSettingsFinder _settingsFinder, 
            ModLinks ml, 
            ApiLinks al, 
            ISettings? settings = null)
        {
            foreach (var mod in ml.Manifests)
            {
                var item = new ModItem
                (
                    settings,
                    link: mod.Links.OSUrl,
                    version: mod.Version.Value,
                    name: mod.Name,
                    shasum: mod.Links.SHA256,
                    description: mod.Description,
                    repository: mod.Repository,
                    issues: mod.Issues,
                    rawReadMeURL: mod.ReadMe,
                    dependencies: mod.Dependencies,
                    
                    tags: mod.Tags,
                    integrations: mod.Integrations,
                    authors: mod.Authors,
                    
                    state: mods.FromManifest(mod)
                    
                );
                
                _items.Add(item);
                _itemNames.Add(mod.Name);
            }

            if (settings is not null)
            {
                foreach (var (externalModName, externalModState) in mods.NotInModlinksMods)
                {
                    if (externalModState.ModlinksMod)
                    {
                        var mod = _items.First(x => x.Name == externalModName);
                        mod.State = externalModState;
                    }
                    else
                    {
                        _items.Add(ModItem.Empty(
                            settings,
                            state: externalModState,
                            name: externalModName,
                            description: "This mod is not from official modlinks"));
                    }
                }
            }

            _items.Sort((a, b) => string.Compare(a.Name, b.Name));
            _items.ForEach(i => i.FindSettingsFile(_settingsFinder));

            Api = (al.Manifest.Links.OSUrl, al.Manifest.Version, al.Manifest.Links.SHA256);
        }

        public ModDatabase(IModSource mods, IGlobalSettingsFinder settingsFinder, (ModLinks ml, ApiLinks al) links, ISettings settings) 
            : this(mods, settingsFinder, links.ml, links.al, settings) { }

        public ModDatabase(IModSource mods, IGlobalSettingsFinder settingsFinder, string modlinks, string apilinks) 
            : this(mods, settingsFinder, FromString<ModLinks>(modlinks), FromString<ApiLinks>(apilinks)) { }
        
        public static async Task<(ModLinks, ApiLinks)> FetchContent(HttpClient hc, ISettings settings, string? gameVersion, bool fetchOfficial = true)
        {
            string linksBase = await FetchLinksBase(hc, settings, gameVersion);
            Trace.WriteLine($"Using links from {linksBase} for game version {gameVersion ?? "unknown"}");

            // although slower to fetch one by one, prevents silent errors and hence resulting in
            // empty screen with no error
            ModLinks ml = await FetchModLinks(hc, settings, linksBase, fetchOfficial);
            ApiLinks al = await FetchApiLinks(hc, settings, linksBase);

            return (ml, al);
        }

        private static async Task<string> FetchLinksBase(HttpClient hc, ISettings settings, string? gameVersion)
        {
            Dictionary<string, string>? map;
            try
            {
                using var cts = new CancellationTokenSource(TIMEOUT);
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    await hc.GetStringAsync2(settings, new Uri(LinksBaseMapUri), cts.Token));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                Trace.WriteLine($"Links base map unavailable, using default links: {e.Message}");
                return LINKS_BASE;
            }

            if (map is null)
                return LINKS_BASE;

            string? linksBase = map.FirstOrDefault(x => GameVersion.Equal(x.Key, gameVersion)).Value;
            if (linksBase is null && !map.TryGetValue("default", out linksBase))
                return LINKS_BASE;

            return linksBase.TrimEnd('/');
        }

        private static Task<string> FetchLinksFile(HttpClient hc, ISettings settings, string linksBase, string file)
        {
            // the CDN fallback only mirrors the default modlinks
            Uri? fallback = linksBase == LINKS_BASE ? new Uri($"{FALLBACK_LINKS_BASE}/{file}") : null;
            return FetchWithFallback(hc, settings, new Uri($"{linksBase}/{file}"), fallback);
        }
        
        public static T FromString<T>(string xml) where T : XmlDataContainer
        {
            var serializer = new XmlSerializer(typeof(T));
            
            using TextReader reader = new StringReader(xml);

            var obj = (T?) serializer.Deserialize(reader);

            if (obj is null)
                throw new InvalidDataException();

            obj.Raw = xml;

            return obj;
        }

        private static async Task<ApiLinks> FetchApiLinks(HttpClient hc, ISettings settings, string linksBase)
        {
            return FromString<ApiLinks>(await FetchLinksFile(hc, settings, linksBase, "ApiLinks.xml"));
        }

        private static async Task<ModLinks> FetchModLinks(HttpClient hc, ISettings settings, string linksBase, bool fetchOfficial)
        {
            if (!fetchOfficial && settings.UseCustomModlinks)
            {
                try
                {
                    var modlinksUri = new Uri(settings.CustomModlinksUri);
                    if (modlinksUri.IsFile)
                    {
                        return FromString<ModLinks>(await File.ReadAllTextAsync(modlinksUri.LocalPath));
                    }

                    var cts = new CancellationTokenSource(TIMEOUT);

                    //get raw versions of common urls
                    Regex githubRegex = new Regex(@"^(http(s?):\/\/)?(www\.)?github.com?");
                    Regex pasteBinRegex = new Regex(@"^(http(s?):\/\/)?(www\.)?pastebin.com?");

                    if (githubRegex.IsMatch(settings.CustomModlinksUri))
                    {
                        settings.CustomModlinksUri = settings.CustomModlinksUri
                            .Replace("github.com", "raw.githubusercontent.com").Replace("/blob/", "/");
                    }
                    if (pasteBinRegex.IsMatch(settings.CustomModlinksUri))
                    {
                        settings.CustomModlinksUri = settings.CustomModlinksUri.Replace("pastebin.com", "pastebin.com/raw");
                    }
                    
                    return FromString<ModLinks>(await hc.GetStringAsync2(settings, new Uri(settings.CustomModlinksUri), cts.Token));
                }
                catch (Exception e)
                {
                    Trace.TraceError($"Unable to load custom modlinks because {e}");
                    throw new InvalidModlinksException();
                }
            }

            return FromString<ModLinks>(await FetchLinksFile(hc, settings, linksBase, "ModLinks.xml"));

        }

        private static async Task<string> FetchWithFallback(HttpClient hc, ISettings? settings, Uri uri, Uri? fallback)
        {
            try
            {
                var cts = new CancellationTokenSource(TIMEOUT);
                return await hc.GetStringAsync2(settings, uri, cts.Token);
            }
            catch (Exception e) when (fallback is not null && e is TaskCanceledException or HttpRequestException)
            {
                var cts = new CancellationTokenSource(TIMEOUT);
                return await hc.GetStringAsync2(settings, fallback, cts.Token);
            }
        }

        public static async Task<string> FetchVanillaAssemblyLink(ISettings? settings, string? gameVersion)
        {
            var cts = new CancellationTokenSource(TIMEOUT);
            var hc = new HttpClient();
            hc.DefaultRequestHeaders.Add("User-Agent", "Lumafly");
            var json = JsonDocument.Parse(await hc.GetStringAsync2(settings, VanillaApiRepo, cts.Token));

            var platform = "Windows";
            if (OperatingSystem.IsMacOS()) platform = "Mac";
            if (OperatingSystem.IsLinux()) platform = "Linux";

            if (json.RootElement.TryGetProperty("by_version", out var byVersion))
            {
                foreach (var version in byVersion.EnumerateObject())
                {
                    if (GameVersion.Equal(version.Name, gameVersion)
                        && version.Value.TryGetProperty(platform, out var linkElem)
                        && linkElem.GetString() is { } link)
                        return link;
                }
            }

            throw new ReadableError(
                $"Lumafly has no vanilla assembly for Hollow Knight {gameVersion ?? "unknown"}. " +
                "Please verify integrity of game files instead.");
        }
    }

    public class InvalidModlinksException : Exception
    {
        public InvalidModlinksException() { } 
    }
}