using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xnovaa.App.Services;

/// <summary>Shared JSON load/save helpers plus the %AppData%\Xnovaa data folder.</summary>
public static class AppPaths
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Xnovaa");

    public static string LibraryFile => Path.Combine(DataFolder, "library.json");
    public static string PlaylistsFile => Path.Combine(DataFolder, "playlists.json");
    public static string StateFile => Path.Combine(DataFolder, "state.json");
    public static string ThumbnailsFolder => Path.Combine(DataFolder, "thumbnails");

    public static void EnsureFolders()
    {
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(ThumbnailsFolder);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (!File.Exists(path)) return new T();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new T();
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(DataFolder, "error.log"),
                    $"[Load<{typeof(T).Name}>] {path}: {ex.Message}\n");
            }
            catch { }
            return new T();
        }
    }

    public static void Save<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
        {
            JsonSerializer.Serialize(stream, data, JsonOptions);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
