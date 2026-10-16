using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using SinmaiAlpha.Assets;

internal static class Program
{
    static void Require(bool value, string name) { if (!value) throw new Exception(name); System.Console.WriteLine("PASS " + name); }
    static void Main(string[] args)
    {
        var xml = new XmlDocument();
        xml.LoadXml("<MusicData><name><id>16099</id><str>Metadata test</str></name><notesData><Notes><file><path>016099_00.ma2</path></file><level>12</level><X-SinmaiAlpha><Difficulty>Astral</Difficulty><AllowTapInHold>false</AllowTapInHold><CustomLevelText>true</CustomLevelText><LevelText>LV&lt;15+ &amp; 狂&gt;</LevelText><Future>keep</Future></X-SinmaiAlpha></Notes></notesData></MusicData>");
        // The game's serialization type must run inside Unity/Mono. The regular
        // console run covers the shared reader without loading native Unity calls.
        if (Array.IndexOf(args, "--native") >= 0) CheckNative(xml);
        var element = (XmlElement)xml.SelectSingleNode("/MusicData/notesData/Notes/X-SinmaiAlpha");
        var flags = ChartFlags.FromXml(element);
        Require(flags.Theme == "Astral" && flags.TapInHoldOverride == false && flags.CustomLevelText && flags.LevelText == "LV<15+ & 狂>", "net472 reads XML flags and escaped text");
        flags.HasTapInHoldOverride = false; flags.WriteXml(element);
        Require(ChartFlags.FromXml(element).TapInHoldOverride == null && element.SelectSingleNode("Future").InnerText == "keep", "XML update preserves unknown nodes and default override");
        var empty = new XmlDocument().CreateElement("X-SinmaiAlpha");
        Require(ChartFlags.FromXml(empty).Theme == "None" && ChartFlags.Parse("").Theme == "Strong", "empty XML and empty legacy flags keep different semantics");
        var root = Path.Combine(Path.GetTempPath(), "alpha-xml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            xml.Save(Path.Combine(root, "Music.xml"));
            var chart = Path.Combine(root, "016099_00.ma2");
            File.WriteAllText(ChartMetadata.MarkerPath(chart), "Inscribed\nAllowTapInHold=true");
            Require(ChartMetadata.Read(chart, 0).Theme == "Astral" && ChartMetadata.Read(chart, 0).TapInHoldOverride == null, "XML wins over conflicting legacy fields");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(root).StartsWith("alpha-xml-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
    private static void CheckNative(XmlDocument xml)
    {
        using (var reader = new XmlNodeReader(xml))
        {
            var native = (Manager.MaiStudio.Serialize.MusicData)new XmlSerializer(typeof(Manager.MaiStudio.Serialize.MusicData)).Deserialize(reader);
            Require(native.name.id == 16099 && native.notesData.Count == 1 && native.notesData[0].file.path == "016099_00.ma2" && native.notesData[0].level == 12, "native game MusicData deserializer accepts extension and preserves native fields");
        }
    }
}
