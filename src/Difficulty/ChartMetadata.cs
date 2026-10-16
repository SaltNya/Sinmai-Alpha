using System;
using System.IO;
using System.Xml;

namespace SinmaiAlpha.Assets;

// Music.xml owns metadata per Notes entry. Legacy markers are read only when
// that entry has no extension, including an explicitly empty extension.
public static class ChartMetadata
{
    public const string ElementName = "X-SinmaiAlpha";
    public static string MarkerPath(string chart) => Path.ChangeExtension(chart, ".ExtraDifficulty.flag");

    public static ChartFlags ReadLegacy(string chart, int difficulty)
    {
        var marker = MarkerPath(chart);
        if (!File.Exists(marker) && difficulty == 3)
            marker = Path.Combine(Path.GetDirectoryName(chart), "ExtraDifficulty.flag");
        return File.Exists(marker) ? ChartFlags.Parse(File.ReadAllText(marker)) : null;
    }

    public static ChartFlags Read(string chart, int difficulty, Action<string> warning = null)
    {
        if (string.IsNullOrWhiteSpace(chart) || difficulty < 0 || difficulty > 4) return null;
        try
        {
            var fullPath = Path.GetFullPath(chart);
            var directory = new DirectoryInfo(Path.GetDirectoryName(fullPath));
            while (directory != null)
            {
                var xmlPath = Path.Combine(directory.FullName, "Music.xml");
                if (File.Exists(xmlPath))
                {
                    var document = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(xmlPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                        document.Load(reader);
                    var notes = document.SelectNodes("/MusicData/notesData/Notes");
                    if (notes != null && difficulty < notes.Count)
                    {
                        var note = notes[difficulty];
                        var name = note.SelectSingleNode("file/path")?.InnerText;
                        if (!string.IsNullOrWhiteSpace(name) && string.Equals(fullPath,
                            Path.GetFullPath(Path.Combine(directory.FullName, name.Replace('\\', Path.DirectorySeparatorChar))), StringComparison.OrdinalIgnoreCase))
                        {
                            var metadata = note.SelectSingleNode(ElementName);
                            if (metadata != null) return ChartFlags.FromXml(metadata);
                        }
                    }
                    // A nearby unrelated Music.xml must not lend its settings.
                    break;
                }
                if (directory.Name.Equals("StreamingAssets", StringComparison.OrdinalIgnoreCase) ||
                    directory.Name.Equals("music", StringComparison.OrdinalIgnoreCase)) break;
                directory = directory.Parent;
            }
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is XmlException || error is ArgumentException || error is NotSupportedException)
        {
            warning?.Invoke("Cannot read chart metadata: " + error.Message);
        }
        return ReadLegacy(chart, difficulty);
    }
}
