using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLine.Core.Settings;

public enum InputKind
{
    /// <summary>A capture endpoint: microphone, line in, interface input.</summary>
    Device,
    /// <summary>One app (and its child processes) via per-process loopback.</summary>
    App,
    /// <summary>Everything rendered on the system except OpenLine itself.</summary>
    System,
}

public static class DeviceIds
{
    /// <summary>Follow whatever Windows has set as the default device.</summary>
    public const string Default = "default";

    public static bool IsDefault(string? id) => id is null || id == Default;
}

public sealed class InputConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Input";
    public InputKind Kind { get; set; }
    /// <summary>Capture endpoint id; null or <see cref="DeviceIds.Default"/> = Windows default.</summary>
    public string? DeviceId { get; set; }
    /// <summary>Process name without .exe (Kind = App).</summary>
    public string? ProcessName { get; set; }
    public double Gain { get; set; } = 1.0;
    public bool Muted { get; set; }
    public int ColorIndex { get; set; }
}

public sealed class RouteConfig
{
    public Guid InputId { get; set; }
    public double Gain { get; set; } = 1.0;
}

public sealed class MixConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Mix";
    /// <summary>Render endpoint id, <see cref="DeviceIds.Default"/>, or null = not assigned yet.</summary>
    public string? DeviceId { get; set; }
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; }
    public List<RouteConfig> Routes { get; set; } = [];
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public List<InputConfig> Inputs { get; set; } = [];
    public List<MixConfig> Mixes { get; set; } = [];
    /// <summary>Where "Listen" plays; null = Windows default output.</summary>
    public string? MonitorDeviceId { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool FirstRunDone { get; set; }
}

/// <summary>
/// Portable: settings live next to the exe. Falls back to %LOCALAPPDATA%\OpenLine when that
/// folder isn't writable (e.g. dropped into Program Files).
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; }

    public SettingsStore()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "OpenLine.settings.json");
        FilePath = CanWrite(AppContext.BaseDirectory)
            ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenLine", "OpenLine.settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt file: keep a copy for the user, start clean.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".openline-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
