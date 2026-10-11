using System.Text.Json;

namespace TinyMouseMacro;

internal sealed record WindowPlacement(int X, int Y, int Width, int Height, int Dpi = 96,
    bool Maximized = false, string? Monitor = null)
{
    internal bool IsValid => Width is > 0 and <= 100000 && Height is > 0 and <= 100000
        && X is >= -100000 and <= 100000 && Y is >= -100000 and <= 100000 && Dpi is >= 48 and <= 768;

    internal Rectangle FitToWorkingArea(Rectangle area, int dpi, Size minimumSize)
    {
        var width = Math.Min(area.Width, Math.Max(minimumSize.Width, (int)Math.Round(Width * (double)dpi / Dpi)));
        var height = Math.Min(area.Height, Math.Max(minimumSize.Height, (int)Math.Round(Height * (double)dpi / Dpi)));
        return new Rectangle(Math.Clamp(X, area.Left, area.Right - width),
            Math.Clamp(Y, area.Top, area.Bottom - height), width, height);
    }
}

internal sealed class WindowPlacementStore(string path)
{
    internal string FilePath => path;

    internal WindowPlacement? Load()
    {
        foreach (var candidate in new[] { path, path + ".backup" })
        {
            if (Read(candidate) is { } placement) return placement;
        }
        return null;
    }

    private static WindowPlacement? Read(string candidate)
    {
        try
        {
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(candidate));
            return placement is { IsValid: true } ? placement : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    internal void Save(WindowPlacement placement)
    {
        if (!placement.IsValid) throw new ArgumentException("Invalid window placement.", nameof(placement));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, placement);
            stream.Flush(flushToDisk: true);
        }
        // A corrupt primary must not overwrite the good backup we just recovered from.
        if (File.Exists(path)) File.Replace(temporary, path, Read(path) is null ? null : path + ".backup", ignoreMetadataErrors: true);
        else File.Move(temporary, path);
    }
}
