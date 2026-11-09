using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.PirateFin;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PirateFin.Icons;

/// <summary>
/// Profile icon packs, stored under &lt;data&gt;/piratefin/icons: one folder per pack holding its PNG icons and an
/// optional pack.json (display name, order, icon titles / seasons / defaults). Images dropped into a folder without
/// being listed in pack.json still show up, titled after their file name.
/// </summary>
public sealed partial class IconPackService : IIconPackService
{
    private const string ManifestFile = "pack.json";
    private const string UserIconsFile = "user-icons.json";

    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private static readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IServerConfigurationManager _configurationManager;
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<IconPackService> _logger;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IconPackService"/> class.
    /// </summary>
    /// <param name="configurationManager">The server configuration manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="logger">The logger.</param>
    public IconPackService(
        IServerConfigurationManager configurationManager,
        IUserManager userManager,
        IProviderManager providerManager,
        ILogger<IconPackService> logger)
    {
        _configurationManager = configurationManager;
        _userManager = userManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    private string PirateFinPath => Path.Combine(_configurationManager.ApplicationPaths.DataPath, "piratefin");

    private string IconsPath => Path.Combine(PirateFinPath, "icons");

    private static string BundledIconsPath => Path.Combine(AppContext.BaseDirectory, "PirateFin", "icons");

    /// <inheritdoc />
    public void InstallBundledPacks()
    {
        lock (_lock)
        {
            if (Directory.Exists(IconsPath) || !Directory.Exists(BundledIconsPath))
            {
                return;
            }

            var count = 0;
            foreach (var source in Directory.EnumerateFiles(BundledIconsPath, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(IconsPath, Path.GetRelativePath(BundledIconsPath, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                count++;
            }

            _logger.LogInformation("Installed {Count} bundled profile icon files into {Path}", count, IconsPath);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IconPack> GetPacks()
    {
        if (!Directory.Exists(IconsPath))
        {
            return [];
        }

        return Directory.EnumerateDirectories(IconsPath)
            .Select(dir => (Directory: dir, Manifest: ReadManifest(dir)))
            .OrderBy(p => p.Manifest.Order ?? int.MaxValue)
            .ThenBy(p => p.Manifest.Name ?? Path.GetFileName(p.Directory), StringComparer.CurrentCultureIgnoreCase)
            .Select(p => ToPack(p.Directory, p.Manifest))
            .ToList();
    }

    /// <inheritdoc />
    public string? GetIconPath(string packId, string file)
    {
        var dir = GetPackDirectory(packId);
        if (dir is null || !IsIconFileName(file))
        {
            return null;
        }

        var path = Path.Combine(dir, file);
        return File.Exists(path) ? path : null;
    }

    /// <inheritdoc />
    public (string PackId, string File)? GetUserIcon(Guid userId)
    {
        var icons = ReadUserIcons();
        if (icons.TryGetValue(userId.ToString("N", CultureInfo.InvariantCulture), out var value)
            && value.Split('/') is [var packId, var file])
        {
            return (packId, file);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> SetUserIconAsync(User user, string packId, string file, CancellationToken cancellationToken)
    {
        var source = GetIconPath(packId, file);
        if (source is null)
        {
            return false;
        }

        // Same as uploading a profile image (ImageController.PostUserImage), from the pack's file.
        var path = Path.Combine(_configurationManager.ApplicationPaths.UserConfigurationDirectoryPath, user.Username, "profile.png");
        if (user.ProfileImage is not null)
        {
            await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
        }

        user.ProfileImage = new ImageInfo(path);
        var stream = File.OpenRead(source);
        await using (stream.ConfigureAwait(false))
        {
            await _providerManager.SaveImage(stream, "image/png", path).ConfigureAwait(false);
        }

        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        lock (_lock)
        {
            var icons = ReadUserIcons();
            icons[user.Id.ToString("N", CultureInfo.InvariantCulture)] = packId + "/" + file;
            WriteJson(Path.Combine(PirateFinPath, UserIconsFile), icons);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task AssignDefaultIconIfMissingAsync(User user, CancellationToken cancellationToken)
    {
        if (user.ProfileImage is not null)
        {
            return;
        }

        var defaults = GetPacks()
            .SelectMany(p => p.Icons.Where(i => i.Default).Select(i => (Pack: p.Id, i.File)))
            .ToList();
        if (defaults.Count == 0)
        {
            _logger.LogWarning("No default profile icons found in {Path}; {User} keeps no profile image", IconsPath, user.Username);
            return;
        }

        var (pack, file) = defaults[Random.Shared.Next(defaults.Count)];
        await SetUserIconAsync(user, pack, file, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Gave {User} the profile icon {Pack}/{File}", user.Username, pack, file);
    }

    /// <inheritdoc />
    public IconPack CreatePack(string name)
    {
        name = name.Trim();
        lock (_lock)
        {
            var dir = Path.Combine(IconsPath, GetFreeFolderName(name, null));
            Directory.CreateDirectory(dir);
            var manifest = new PackManifest { Name = name, Order = GetNextOrder(), Icons = [] };
            WriteJson(Path.Combine(dir, ManifestFile), manifest);
            return ToPack(dir, manifest);
        }
    }

    /// <inheritdoc />
    public IconPack? RenamePack(string packId, string name)
    {
        name = name.Trim();
        lock (_lock)
        {
            var dir = GetPackDirectory(packId);
            if (dir is null)
            {
                return null;
            }

            var manifest = ReadManifest(dir);
            manifest.Name = name;
            WriteJson(Path.Combine(dir, ManifestFile), manifest);

            var folder = GetFreeFolderName(name, packId);
            if (!string.Equals(folder, packId, StringComparison.Ordinal))
            {
                var target = Path.Combine(IconsPath, folder);
                Directory.Move(dir, target);
                dir = target;
                RenamePackInUserIcons(packId, folder);
            }

            return ToPack(dir, manifest);
        }
    }

    /// <inheritdoc />
    public bool DeletePack(string packId)
    {
        lock (_lock)
        {
            var dir = GetPackDirectory(packId);
            if (dir is null)
            {
                return false;
            }

            Directory.Delete(dir, true);
            return true;
        }
    }

    /// <inheritdoc />
    public async Task<ProfileIcon?> AddIconAsync(string packId, string title, Stream png, CancellationToken cancellationToken)
    {
        var dir = GetPackDirectory(packId);
        if (dir is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await png.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (!buffer.ToArray().AsSpan().StartsWith("\x89PNG"u8))
        {
            throw new ArgumentException("The icon must be a PNG image.", nameof(png));
        }

        title = string.IsNullOrWhiteSpace(title) ? "Icon" : title.Trim();
        lock (_lock)
        {
            var manifest = ReadManifest(dir);
            var icons = ToPack(dir, manifest).Icons.ToList();
            var number = icons.Count + 1;
            string file;
            do
            {
                file = string.Create(CultureInfo.InvariantCulture, $"{number++:000}-{Slug(title)}.png");
            }
            while (File.Exists(Path.Combine(dir, file)));

            #pragma warning disable CA1849 // Can't await in lock block
            File.WriteAllBytes(Path.Combine(dir, file), buffer.ToArray());
            #pragma warning restore CA1849

            var icon = new ProfileIcon { File = file, Title = title };
            icons.Add(icon);
            manifest.Icons = icons.Select(ToEntry).ToList();
            WriteJson(Path.Combine(dir, ManifestFile), manifest);
            return icon;
        }
    }

    /// <inheritdoc />
    public bool DeleteIcon(string packId, string file)
    {
        lock (_lock)
        {
            var path = GetIconPath(packId, file);
            if (path is null)
            {
                return false;
            }

            File.Delete(path);

            var dir = Path.GetDirectoryName(path)!;
            var manifest = ReadManifest(dir);
            manifest.Icons = manifest.Icons?.Where(i => !string.Equals(i.File, file, StringComparison.OrdinalIgnoreCase)).ToList();
            WriteJson(Path.Combine(dir, ManifestFile), manifest);
            return true;
        }
    }

    /// <summary>
    /// Gets the folder name for a pack name; the same rule download_icons.py uses.
    /// </summary>
    /// <param name="name">The pack name.</param>
    /// <returns>The folder name.</returns>
    public static string GetFolderName(string name)
    {
        var folder = InvalidFileChars().Replace(name, "_").Trim(' ', '.');
        return folder.Length == 0 ? "Icons" : folder;
    }

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidFileChars();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    // An icon file name: no folders, a .png.
    [GeneratedRegex(@"^[^<>:""/\\|?*\x00-\x1f]+\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex IconFileName();

    private static bool IsIconFileName(string file) => IconFileName().IsMatch(file) && file != "." && !file.Contains("..", StringComparison.Ordinal);

    private static string Slug(string text)
    {
        var slug = NonSlugChars().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "icon" : slug;
    }

    private static PackEntry ToEntry(ProfileIcon icon) => new() { File = icon.File, Title = icon.Title, Season = icon.Season, Default = icon.Default ? true : null };

    private static IconPack ToPack(string dir, PackManifest manifest)
    {
        var files = Directory.EnumerateFiles(dir, "*.png")
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var icons = new List<ProfileIcon>();
        foreach (var entry in manifest.Icons ?? [])
        {
            if (entry.File is not null && files.Remove(entry.File))
            {
                icons.Add(new ProfileIcon
                {
                    File = entry.File,
                    Title = string.IsNullOrWhiteSpace(entry.Title) ? Path.GetFileNameWithoutExtension(entry.File) : entry.Title,
                    Season = entry.Season,
                    Default = entry.Default == true
                });
            }
        }

        // Images added to the folder by hand.
        icons.AddRange(files
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => new ProfileIcon { File = f, Title = Path.GetFileNameWithoutExtension(f) }));

        return new IconPack
        {
            Id = Path.GetFileName(dir),
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileName(dir) : manifest.Name,
            Icons = icons
        };
    }

    private static PackManifest ReadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestFile);
        if (!File.Exists(path))
        {
            return new PackManifest();
        }

        try
        {
            return JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path)) ?? new PackManifest();
        }
        catch (JsonException)
        {
            return new PackManifest();
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, _jsonOptions));
        File.Move(temp, path, true);
    }

    private string? GetPackDirectory(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId) || packId.Contains("..", StringComparison.Ordinal) || InvalidFileChars().IsMatch(packId))
        {
            return null;
        }

        var dir = Path.Combine(IconsPath, packId);
        return Directory.Exists(dir) ? dir : null;
    }

    private string GetFreeFolderName(string name, string? currentId)
    {
        var baseName = GetFolderName(name);
        var folder = baseName;
        for (var i = 2; Directory.Exists(Path.Combine(IconsPath, folder)) && !string.Equals(folder, currentId, StringComparison.OrdinalIgnoreCase); i++)
        {
            folder = string.Create(CultureInfo.InvariantCulture, $"{baseName} ({i})");
        }

        return folder;
    }

    private int GetNextOrder()
        => Directory.Exists(IconsPath)
            ? Directory.EnumerateDirectories(IconsPath).Select(d => ReadManifest(d).Order ?? -1).DefaultIfEmpty(-1).Max() + 1
            : 0;

    private Dictionary<string, string> ReadUserIcons()
    {
        var path = Path.Combine(PirateFinPath, UserIconsFile);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void RenamePackInUserIcons(string oldId, string newId)
    {
        var icons = ReadUserIcons();
        foreach (var (user, icon) in icons.ToList())
        {
            if (icon.StartsWith(oldId + "/", StringComparison.Ordinal))
            {
                icons[user] = newId + icon[oldId.Length..];
            }
        }

        WriteJson(Path.Combine(PirateFinPath, UserIconsFile), icons);
    }

    private sealed class PackManifest
    {
        public string? Name { get; set; }

        public int? Order { get; set; }

        public List<PackEntry>? Icons { get; set; }
    }

    private sealed class PackEntry
    {
        public string? File { get; set; }

        public string? Title { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Season { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? Default { get; set; }
    }
}
