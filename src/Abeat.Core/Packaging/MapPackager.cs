using System.IO.Compression;
using Abeat.Core.Analysis;
using Abeat.Core.Formats;
using Abeat.Core.Model;

namespace Abeat.Core.Packaging;

public static class MapPackager
{
    /// <summary>Writes a complete, playable map folder (copying audio and cover from the analysis dir)
    /// and optionally a zip next to it for BeatSaver/ArcViewer.</summary>
    public static string Write(MapSet map, SongAnalysis a, string outputFolder, bool zip)
    {
        // drop difficulties from earlier runs that are no longer part of the map
        if (Directory.Exists(outputFolder))
            foreach (var f in Directory.EnumerateFiles(outputFolder, "*.dat")) File.Delete(f);
        MapWriter.WriteFolder(map, outputFolder);
        CopyIfNeeded(Path.Combine(a.Directory, a.Audio.File), Path.Combine(outputFolder, map.SongFile));
        CopyIfNeeded(Path.Combine(a.Directory, a.Cover), Path.Combine(outputFolder, map.CoverFile));
        if (!zip) return outputFolder;

        string zipPath = outputFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip";
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var f in Directory.EnumerateFiles(outputFolder))
            archive.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
        return zipPath;
    }

    static void CopyIfNeeded(string src, string dst)
    {
        if (!File.Exists(src)) return;
        if (Path.GetFullPath(src) == Path.GetFullPath(dst)) return;
        File.Copy(src, dst, overwrite: true);
    }

    /// <summary>File-system safe folder name like BeatSaver's "Artist - Title".</summary>
    public static string FolderName(MapSet map)
    {
        string name = string.IsNullOrWhiteSpace(map.SongAuthor) ? map.SongName : $"{map.SongAuthor} - {map.SongName}";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "map" : name.Trim();
    }
}
