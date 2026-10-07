using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SinmaiAlpha.Assets;

// Mod 与 MCM 共用的文件格式；每个目录是一个可自由添加的难度。
[Serializable]
public sealed class DifficultyTheme
{
    public const DifficultyTheme None=null;
    public string Id;
    public string name;
    public string Name {get=>name;set=>name=value;}
    public string color;
    public string Color {get=>color;set=>color=value;}
    public string textureCode;
    public string TextureCode {get=>textureCode;set=>textureCode=value;}
    public string textureNumber;
    public string TextureNumber {get=>textureNumber;set=>textureNumber=value;}
    public string[] aliases;
    public string[] Aliases {get=>aliases;set=>aliases=value;}
}
public static class DifficultyThemeCatalog
{
    public static List<DifficultyTheme> Read(string root,Func<string,DifficultyTheme> deserialize,Action<string> warning=null)
    {
        var result=new List<DifficultyTheme>();
        if(!Directory.Exists(root))return result;
        foreach(var dir in new DirectoryInfo(root).EnumerateDirectories().OrderBy(d=>d.Name,StringComparer.OrdinalIgnoreCase)) {
            if((dir.Attributes&FileAttributes.ReparsePoint)!=0 || dir.Name.Equals("None",StringComparison.OrdinalIgnoreCase))continue;
            try {
                var file=Path.Combine(dir.FullName,"difficulty.json");
                DifficultyTheme theme=null;
                if(File.Exists(file)) {
                    theme=deserialize(File.ReadAllText(file));
                }
                theme=theme??new DifficultyTheme();theme.Id=dir.Name;
                if(string.IsNullOrWhiteSpace(theme.Name))theme.Name=dir.Name;
                if(theme.Color==null||!Regex.IsMatch(theme.Color,@"^#[0-9a-fA-F]{6}$"))theme.Color="#777777";
                var names=dir.EnumerateFiles("*.png").Select(f=>f.Name).ToArray();
                if(string.IsNullOrWhiteSpace(theme.TextureCode))theme.TextureCode=names.Select(n=>Regex.Match(n,@"^UI_MSS_MBase_([^_]+)_Tab_01\.png$",RegexOptions.IgnoreCase)).FirstOrDefault(m=>m.Success)?.Groups[1].Value;
                if(string.IsNullOrWhiteSpace(theme.TextureNumber))theme.TextureNumber=names.Select(n=>Regex.Match(n,@"^UI_NUM_MLevel_([0-9]+)\.png$",RegexOptions.IgnoreCase)).FirstOrDefault(m=>m.Success)?.Groups[1].Value;
                if(theme.TextureCode==null||!Regex.IsMatch(theme.TextureCode,@"^[A-Za-z0-9]+$"))continue;
                if(theme.TextureNumber!=null&&!Regex.IsMatch(theme.TextureNumber,@"^[0-9]+$"))theme.TextureNumber=null;
                result.Add(theme);
            } catch(Exception e) when(e is IOException||e is UnauthorizedAccessException||e is ArgumentException||e.GetType().Name=="JsonException") {warning?.Invoke(dir.Name+": "+e.Message);}
        }
        return result;
    }
    public static DifficultyTheme Resolve(IEnumerable<DifficultyTheme> themes,string value)
    {
        value=(value??"").Trim().Trim('\uFEFF').Trim();
        if(value.Length==0)value="Strong"; // 旧版空标记的兼容约定。
        if(value.Equals("None",StringComparison.OrdinalIgnoreCase))return null;
        var all=themes.ToArray();
        return all.FirstOrDefault(t=>t.Id.Equals(value,StringComparison.OrdinalIgnoreCase))??
            all.FirstOrDefault(t=>t.Aliases?.Any(a=>string.Equals(a,value,StringComparison.OrdinalIgnoreCase))==true);
    }
}
