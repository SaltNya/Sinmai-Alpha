using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Assets;
using Theme=SinmaiAlpha.Assets.DifficultyTheme;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.MusicSelect.ChainList;
using Monitor.MusicSelect.UI;
using Process;
using UI;
using UI.DaisyChainList;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Difficulty;


public static partial class ExtraDifficulty
{

    public static readonly string strongTextureDir = "Sinmai-Alpha/ExtraDifficulty";

    public static readonly bool taggedMasterSkin = true;
    public const string MarkerFileName = "ExtraDifficulty.flag";

    private const int Master = 3;
    private static List<Theme> themes;
    private static List<Theme> Themes=>themes??(themes=DifficultyThemeCatalog.Read(FileSystem.ResolvePath(strongTextureDir),JsonUtility.FromJson<Theme>,m=>MelonLogger.Warning("[ExtraDifficulty] "+m)));
    private sealed class MusicIdentity { public int Id = -1; public int Difficulty = -1; public bool Utage; }
    private sealed class ImageLease { public Sprite Original, Applied; }
    private sealed class SlotLease { public Sprite Original, Applied, VisibleOriginal; public int Index = -1; }
    private static readonly Dictionary<string, Theme> Markers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Sprite> Sprites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<MusicChainCardObejct, MusicIdentity> Cards = new();
    private static readonly ConditionalWeakTable<MusicInfomationController, MusicIdentity> Information = new();
    private static readonly ConditionalWeakTable<KOP_ResultTrackData, MusicIdentity> ResultTracks = new();
    private static readonly ConditionalWeakTable<Image, ImageLease> Images = new();
    private static readonly ConditionalWeakTable<MultipleImage, SlotLease> Slots = new();
    [ThreadStatic] private static Theme levelTheme;
    [ThreadStatic] private static MusicIdentity menuIdentity;
    private static readonly Dictionary<Theme, Sprite[]> NumberSheets = new();
    private static readonly HashSet<Theme> NumberSheetsChecked = new();
    private static bool loaded;
    private static bool useLegacyUpperFrame;

    public static void OnBeforePatch()
    {
        Markers.Clear(); themes=null; LevelFlags.Clear(); LevelTextWarnings.Clear();
        // Read the running game's constant through reflection: a direct reference
        // would bake the build-time 1.70 value into this DLL. SDEZ 1.70 uses 27000.
        try
        {
            var version = typeof(MAI2System.ConstParameter).GetField("NowGameVersion", BindingFlags.Public | BindingFlags.Static);
            useLegacyUpperFrame = version != null && Convert.ToUInt32(version.GetRawConstantValue()) < 27000u;
        }
        catch (Exception error)
        {
            useLegacyUpperFrame = false;
            MelonLogger.Warning("[ExtraDifficulty] Cannot detect upper-frame layout, using current textures: " + error.Message);
        }
    }
    private static bool HasMarker(string chartPath) => MarkerTheme(chartPath) != Theme.None;
    private static Theme MarkerTheme(string chartPath, int difficulty = Master)
    {
        if (string.IsNullOrWhiteSpace(chartPath)) return Theme.None;
        try
        {
            var folder = Path.GetDirectoryName(FileSystem.ResolvePath(chartPath));
            if (string.IsNullOrEmpty(folder)) return Theme.None;
            var chartKey = FileSystem.ResolvePath(chartPath);
            var cacheKey = chartKey + "|" + difficulty;
            if (Markers.TryGetValue(cacheKey, out var marked)) return marked;
            var marker = Path.ChangeExtension(chartKey, ".ExtraDifficulty.flag");
            if (!File.Exists(marker) && difficulty == Master) marker = Path.Combine(folder, MarkerFileName);
            marked = Theme.None;
            if (File.Exists(marker))
            {
                var value = ChartFlags.Parse(File.ReadAllText(marker)).Theme;
                marked=DifficultyThemeCatalog.Resolve(Themes,value);
                if(marked==null && !value.Equals("None",StringComparison.OrdinalIgnoreCase))MelonLogger.Warning("[ExtraDifficulty] Unknown theme in "+marker+": "+value+"; native appearance retained");
            }
            Markers[cacheKey] = marked;
            return marked;
        }
        catch (ArgumentException) { return Theme.None; }
        catch (NotSupportedException) { return Theme.None; }
        catch (IOException error) { MelonLogger.Warning("[ExtraDifficulty] Marker read failed: " + error.Message); return Theme.None; }
        catch (UnauthorizedAccessException error) { MelonLogger.Warning("[ExtraDifficulty] Marker read failed: " + error.Message); return Theme.None; }
    }
    private static bool IsTagged(MusicIdentity identity, int difficulty) => ThemeFor(identity, difficulty) != Theme.None;
    private static Theme ThemeFor(MusicIdentity identity, int difficulty)
    {
        if (!Settings.Current.ExtraDifficulty || !taggedMasterSkin || identity == null || identity.Utage || difficulty < 0 || difficulty > 4 || identity.Id <= 0 || identity.Id >= 100000) return Theme.None;
        // Use the game's winning music record, including Opt overrides. An
        // identically numbered song in another package cannot lend its flag.
        var music = Singleton<DataManager>.Instance.GetMusic(identity.Id);
        if (music?.notesData == null || music.notesData.Count <= difficulty) return Theme.None;
        var theme = MarkerTheme(music.notesData[difficulty]?.file?.path, difficulty);
        if (theme == Theme.None) return theme;
        LoadTextures();
        return theme;
    }
    private static T Field<T>(object owner, string name) => Traverse.Create(owner).Field(name).GetValue<T>();
    private static Sprite SpriteFor(string name,Theme theme)
    {
        if(theme==null||name==null)return null;
        // Central resolution also covers the generic Image/MultipleImage scans
        // and the result animation callback, not only SetDifficulty.
        if (useLegacyUpperFrame && name == "UI_UPE_MBase_" + ThemeCode(theme))
        {
            var legacyName = name + "_Old";
            if (Sprites.TryGetValue(theme.Id + "/" + legacyName, out var legacy) ||
                Sprites.TryGetValue("/" + legacyName, out legacy)) return legacy;
        }
        if(Sprites.TryGetValue(theme.Id+"/"+name,out var sprite))return sprite;
        return Sprites.TryGetValue("/"+name,out sprite)?sprite:null;
    }

    private static void LoadTextures()
    {
        if (loaded) return;
        loaded = true;
        var folder = FileSystem.ResolvePath(strongTextureDir);
        if (!Directory.Exists(folder)) { MelonLogger.Warning("[ExtraDifficulty] Texture directory missing: " + folder); return; }
        // Load legacy root first; the new theme folders override matching files.
        LoadFolder(folder, "");
        foreach (var theme in Themes)
            LoadFolder(Path.Combine(folder, theme.Id),theme.Id);
    }
    private static void LoadFolder(string folder,string themeId)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (!Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file).StartsWith("_dump_")) continue;
            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(File.ReadAllBytes(file))) { Object.Destroy(texture); continue; }
                PrepareTextureEdges(texture);
                var name = Path.GetFileNameWithoutExtension(file);
                var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect);
                sprite.name = name;
                var key=themeId+"/"+name;
                if (Sprites.TryGetValue(key, out var previous)) { Object.Destroy(previous.texture); Object.Destroy(previous); }
                Sprites[key] = sprite;
            }
            catch (Exception error) { if (texture != null) Object.Destroy(texture); MelonLogger.Warning("[ExtraDifficulty] " + Path.GetFileName(file) + ": " + error.Message); }
        }
    }
    private static string ThemeCode(Theme theme) => theme?.TextureCode;
    private static string ThemeNumber(Theme theme) => theme?.TextureNumber;
    private static Sprite[] NumberSheet(Theme theme)
    {
        if (theme == Theme.None) return null;
        if (NumberSheets.TryGetValue(theme, out var numberSheet)) return numberSheet;
        if (!NumberSheetsChecked.Add(theme)) return null;
        LoadTextures();
        var name = "UI_NUM_MLevel_" + ThemeNumber(theme);
        var source = SpriteFor(name,theme);
        if (source == null || source.texture == null) return null;
        var texture = source.texture;
        if (texture.width != 192 || texture.height != 240)
        { MelonLogger.Warning("[ExtraDifficulty] " + name + " must be 192x240; native numbers retained"); return null; }
        numberSheet = new Sprite[15];
        for (var i = 0; i < numberSheet.Length; i++)
        {
            numberSheet[i] = Sprite.Create(texture, new Rect(i % 4 * 48, 240 - (i / 4 + 1) * 60, 48, 60),
                new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect);
            numberSheet[i].name = name + "_" + i;
        }
        NumberSheets[theme] = numberSheet;
        return numberSheet;
    }
    private static Sprite[] NativeSheet(int difficulty, bool utage = false)
    {
        if (utage) return CommonPrefab.GetUtageMusicLevelSprites();
        var table = Field<MusicDifficultySheet>(typeof(CommonPrefab), "_musicLevelSheetTable");
        if (table?.MusicLevelSprites == null || difficulty < 0 || difficulty >= table.MusicLevelSprites.Count) return null;
        return table.MusicLevelSprites[difficulty].Sheet;
    }
    private static string ThemeName(string name, Theme theme)
    {
        var code = ThemeCode(theme);
        if (code == null || string.IsNullOrEmpty(name) || name.Contains("TitleTextImage")) return null;
        // Restrict mappings to difficulty artwork. Unrelated *_Text and Lv
        // images must not be replaced with a song-card label.
        foreach (var native in new[] { "MST_Re", "MST", "EXP", "ADV", "BSC", "BAS" })
        {
            if (name.Contains("_" + native + "_")) return name.Replace("_" + native + "_", "_" + code + "_");
            if (name.EndsWith("_" + native, StringComparison.Ordinal)) return name.Substring(0, name.Length - native.Length) + code;
        }
        // UI_MSS_Btn_01_* is shared by difficulty and option controls.
        // Only DifficultyButtonPatch may replace those button backgrounds.
        return null;
    }
    private static string DifficultyButtonName(string name, Theme theme)
    {
        var number = ThemeNumber(theme);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(number)) return null;
        for (var i = 1; i <= 5; i++)
        {
            var prefix = "UI_MSS_Btn_01_" + i.ToString("D2") + "_";
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return "UI_MSS_Btn_01_" + number + "_" + name.Substring(prefix.Length);
        }
        return null;
    }
    private static bool IsDifficultyButtonBase(Image image)
    {
        // Include inactive controls populated before the select screen opens.
        foreach (var button in image.GetComponentsInParent<DifficultyButtonObject>(true))
            if (Field<Image>(button, "_baseImage") == image) return true;
        return false;
    }
    [HarmonyPatch(typeof(DifficultyButtonObject), "ChangeDifficulty")]
    public static class DifficultyButtonPatch
    {
        [HarmonyPostfix] public static void Postfix(DifficultyButtonObject __instance, Sprite __1, int __3)
        {
            // The button leads to an adjacent difficulty, not the selected one.
            var theme = ThemeFor(Identity(__instance), __3);
            ApplyImage(Field<Image>(__instance, "_baseImage"), SpriteFor(DifficultyButtonName(__1?.name, theme), theme));
        }
    }
    private static void ApplyImage(Image image, Sprite replacement)
    {
        if (image == null) return;
        var lease = Images.GetValue(image, _ => new ImageLease());
        // Restore only the sprite we own. Native reinitialization may already
        // have supplied another difficulty/song's correct sprite.
        if (lease.Applied != null && image.sprite == lease.Applied) image.sprite = lease.Original;
        lease.Applied = null;
        if (replacement == null) return;
        lease.Original = image.sprite; lease.Applied = replacement; image.sprite = replacement;
    }
    private static void ApplySlot(MultipleImage image, Theme theme, string explicitName = null, bool forceVisible = false, int slot = Master)
    {
        if (image == null || image.MultiSprites == null) return;
        var list = image.MultiSprites; var lease = Slots.GetValue(image, _ => new SlotLease());
        var oldReplacement = lease.Applied;
        if (oldReplacement != null && lease.Index >= 0 && lease.Index < list.Count && list[lease.Index] == oldReplacement) list[lease.Index] = lease.Original;
        if (oldReplacement != null && image.sprite == oldReplacement) image.sprite = lease.VisibleOriginal;
        lease.Applied = null;
        if (slot < 0 || slot >= list.Count) return;
        var replacement = theme != Theme.None ? SpriteFor(explicitName ?? ThemeName(list[slot]?.name, theme),theme) : null;
        if (replacement == null) return;
        lease.Index = slot; lease.Original = list[slot]; lease.Applied = replacement; list[slot] = replacement;
        lease.VisibleOriginal = lease.Original;
        if (image.sprite == lease.Original || forceVisible)
        { lease.VisibleOriginal = image.sprite; image.sprite = replacement; }
    }
    private static MusicIdentity Selected(IMusicSelectProcess process)
    {
        var data = process?.GetCombineMusic(0);
        return new MusicIdentity { Id = data == null ? -1 : data.GetID(process.ScoreType) };
    }
    private static MusicIdentity MonitorIdentity(MonitorBase monitor)
    {
        if (monitor == null) return new MusicIdentity();
        if (monitor is MusicSelectMonitor selection) return Selected(Field<IMusicSelectProcess>(selection, "_musicSelect"));
        var index = monitor.MonitorIndex;
        if ((monitor is GameMonitor || monitor is ResultMonitor) && GameManager.MusicTrackNumber > 0)
        {
            var score = Singleton<GamePlayManager>.Instance.GetGameScore(index);
            if (score != null && score.SessionInfo.musicId > 0)
            {
                var session = score.SessionInfo;
                return new MusicIdentity { Id = session.musicId, Difficulty = session.difficulty, Utage = session.musicId >= 100000 };
            }
        }
        var music = GameManager.SelectMusicID; var difficulty = GameManager.SelectDifficultyID;
        return new MusicIdentity { Id = music != null && index >= 0 && index < music.Length ? music[index] : -1,
            Difficulty = difficulty != null && index >= 0 && index < difficulty.Length ? difficulty[index] : -1,
            Utage = GameManager.IsUtage };
    }
    private static MusicIdentity Identity(Component owner)
    {
        if (owner == null) return new MusicIdentity();
        // Sinmai summary cards are KOP_ResultTrackData instances, including
        // cloned prefabs whose transform names do not identify their track.
        var resultTrack = FindResultTrack(owner);
        if (resultTrack != null)
            return ResultTracks.TryGetValue(resultTrack, out var trackIdentity) ? trackIdentity : new MusicIdentity();
        var monitor = owner.GetComponentsInParent<MonitorBase>(true).FirstOrDefault();
        var card = owner.GetComponentsInParent<MusicChainCardObejct>(true).FirstOrDefault();
        if (card != null) return Cards.TryGetValue(card, out var identity) ? identity : new MusicIdentity();
        var information = owner.GetComponentsInParent<MusicInfomationController>(true).FirstOrDefault();
        if (information != null && Information.TryGetValue(information, out var info)) return info;
        return MonitorIdentity(monitor);
    }
    private static KOP_ResultTrackData FindResultTrack(Component owner)
    {
        // Summary windows are populated while inactive. The singular parent
        // lookup skips inactive ancestors on the game's Unity 2018 runtime.
        var parents = owner.GetComponentsInParent<KOP_ResultTrackData>(true);
        return parents.Length == 0 ? null : parents[0];
    }
    private static void Register(MusicChainCardObejct card, MusicIdentity identity)
    {
        if (card == null) return;
        Cards.Remove(card); Cards.Add(card, identity);
        var mini = Field<MusicChainCardObejct>(card, "_miniCard");
        if (mini != null) { Cards.Remove(mini); Cards.Add(mini, identity); }
        var theme = ThemeFor(identity, identity.Difficulty); var code = ThemeCode(theme);
        ApplySlot(Field<MultipleImage>(card, "_difficultyText"), theme, "UI_MSS_MBase_" + code + "_Text", false, Math.Max(0, identity.Difficulty));
        ApplySlot(Field<MultipleImage>(card, "_difficultyBase"), theme, "UI_MSS_MBase_LvBase_" + code, false, Math.Max(0, identity.Difficulty));
    }
    private static void PrepareNumbers(object owner, int difficulty, bool utage, Theme theme)
    {
        BindLevelText(owner, difficulty, utage, theme);
        var sheet = NumberSheet(theme) ?? NativeSheet(difficulty, utage);
        if (sheet == null) return;
        foreach (var name in new[] { "_digitLevel", "_doubleDigitLevel", "_difficultySingle", "_difficultyDouble", "_singleLevel", "_doubleLevel" })
        {
            var counter = Field<SpriteCounter>(owner, name);
            if (counter == null) continue;
            counter.SetSpriteSheet(sheet);
            // SetAllDirty does not recompute SpriteCounter's stored UVs.
            var text = Field<string>(counter, "mainText");
            if (text != null) counter.ChangeText(text);
        }
        var image = Field<Image>(owner, "_levelTextImage");
        if (image != null && sheet.Length > 14) image.sprite = sheet[14];
    }
    private static void Scan(MonitorBase monitor)
        => ScanImages(monitor);
    private static void ScanImages(Component owner)
    {
        if (owner == null) return;
        foreach (var image in owner.GetComponentsInChildren<Image>(true))
        {
            // Owned by ChangeDifficulty with the button's destination slot.
            if (IsDifficultyButtonBase(image) || IsDifficultyHeaderImage(image)) continue;
            var identity = Identity(image); var theme = ThemeFor(identity, identity.Difficulty);
            if (image is MultipleImage multiple)
            {
                var kopHistory = FindResultTrack(image) != null && multiple.MultiSprites != null && multiple.MultiSprites.Count > Master &&
                    multiple.MultiSprites[Master]?.name.Contains("KopMBase") == true;
                ApplySlot(multiple, theme, null, kopHistory, kopHistory ? Master : Math.Max(0, identity.Difficulty)); continue;
            }
            if (Images.TryGetValue(image, out var lease) && image.sprite == lease.Applied) ApplyImage(image, null);
            ApplyImage(image, SpriteFor(ThemeName(image.sprite?.name, theme),theme));
        }
    }
    [HarmonyPatch(typeof(Graphic), "OnDidApplyAnimationProperties")]
    public static class ResultAnimationPatch
    {
        [HarmonyPostfix] public static void Postfix(Graphic __instance)
        {
            // The result Timeline writes Image.m_Sprite every evaluation. Its
            // animation callback runs after that write and before UI rendering.
            // Keep the original clip and its layout/color/timing tracks intact.
            if (!(__instance is Image image) || (image.GetComponentInParent<ResultMonitor>() == null &&
                FindResultTrack(image) == null)) return;
            var identity = Identity(image);
            var theme = ThemeFor(identity, identity.Difficulty);
            if (image is MultipleImage multiple)
            {
                var kopHistory = multiple.MultiSprites != null && multiple.MultiSprites.Count > Master &&
                    multiple.MultiSprites[Master]?.name.Contains("KopMBase") == true;
                ApplySlot(multiple, theme, null, kopHistory, kopHistory ? Master : Math.Max(0, identity.Difficulty));
                return;
            }
            if (Images.TryGetValue(image, out var lease) && image.sprite == lease.Applied) ApplyImage(image, null);
            ApplyImage(image, SpriteFor(ThemeName(image.sprite?.name, theme),theme));
        }
    }
    private static IEnumerator ScanLater(MonitorBase monitor, MusicIdentity identity)
    {
        foreach (var delay in new[] { .6f, 1.2f })
        {
            yield return new WaitForSeconds(delay);
            if (monitor == null) yield break;
            var current = MonitorIdentity(monitor);
            if (current.Id != identity.Id || current.Difficulty != identity.Difficulty) yield break;
            Scan(monitor);
        }
    }

    [HarmonyPatch(typeof(MusicSelectChainList), "SetChainData")]
    public static class CardDataPatch
    {
        [HarmonyPrefix] public static void Prefix(MusicSelectChainList __instance, MusicChainCardObejct __0, int __1, int __3)
        {
            var identity = new MusicIdentity { Difficulty = __3 };
            // Clear pooled ownership before resolving, including negative list
            // offsets. A failed lookup must never retain the preceding song.
            Register(__0, identity);
            var process = Field<IMusicSelectProcess>(__instance, "SelectProcess");
            var data = process?.GetCombineMusic(__1);
            identity.Id = data == null ? -1 : data.GetID(process.ScoreType); identity.Utage = identity.Id >= 100000;
            Register(__0, identity);
        }
    }
    [HarmonyPatch(typeof(KOP_ResultTrackData), "SetMusicData")]
    public static class ResultTrackDataPatch
    {
        [HarmonyPrefix] public static void Prefix(KOP_ResultTrackData __instance, int __0)
        {
            var identity = new MusicIdentity();
            var parents = __instance.GetComponentsInParent<MonitorBase>(true);
            var monitor = parents.Length == 0 ? null : parents[0];
            if (monitor != null && monitor.MonitorIndex >= 0 && monitor.MonitorIndex < 4 && __0 >= 0)
            {
                // SetMusicData receives Sinmai's zero-based track index. Read
                // that player's historical session, never the current song.
                var score = Singleton<GamePlayManager>.Instance.GetGameScore(monitor.MonitorIndex, __0);
                if (score != null)
                {
                    var session = score.SessionInfo;
                    identity.Id = session.musicId; identity.Difficulty = session.difficulty;
                    identity.Utage = session.musicId >= 100000;
                }
            }
            ResultTracks.Remove(__instance); ResultTracks.Add(__instance, identity);
        }
    }
    [HarmonyPatch]
    public static class MenuPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        { yield return AccessTools.Method(typeof(MenuChainList), "Deploy"); yield return AccessTools.Method(typeof(MenuChainList), "ChangeDifficulty"); }
        [HarmonyPrefix] private static void Prefix(MenuChainList __instance, out MusicIdentity __state)
        { __state = menuIdentity; menuIdentity = Selected(Field<IMusicSelectProcess>(__instance, "SelectProcess")); }
        [HarmonyFinalizer] private static void Finalizer(MusicIdentity __state) => menuIdentity = __state;
    }
    [HarmonyPatch(typeof(MusicChainCardObejct), "SetMusicData")]
    public static class CentralCardPatch
    {
        [HarmonyPrefix] public static void Prefix(MusicChainCardObejct __instance, int __5)
        {
            // SetChainData may receive difficulty=-1; Sinmai resolves it before
            // SetMusicData. Store that resolved slot before SetDifficulty renders.
            if (menuIdentity != null && __instance.GetComponentsInParent<MenuCardObject>(true).Length != 0)
                Register(__instance, new MusicIdentity { Id = menuIdentity.Id, Difficulty = __5, Utage = menuIdentity.Id >= 100000 });
            else if (Cards.TryGetValue(__instance, out var identity))
                Register(__instance, new MusicIdentity { Id = identity.Id, Difficulty = __5, Utage = identity.Utage });
        }
    }
    [HarmonyPatch(typeof(MusicChainCardObejct), "SetDifficulty")]
    public static class CardBackgroundPatch
    {
        [HarmonyPostfix] public static void Postfix(MusicChainCardObejct __instance, Sprite __0, Sprite __1, Sprite __2)
        {
            var identity = Identity(__instance);
            var theme = ThemeFor(identity, identity.Difficulty);
            // Follow the game's actual sprite names. Front/back are Deluxe/Standard,
            // not guaranteed Tab_01/Tab_02; mini cards also pass their own base sprite.
            ApplyImage(Field<Image>(__instance, "_background"), SpriteFor(ThemeName(__0?.name,theme),theme));
            ApplyImage(Field<Image>(__instance, "_tagBaseFront"), SpriteFor(ThemeName(__1?.name,theme),theme));
            ApplyImage(Field<Image>(__instance, "_tagBaseBack"), SpriteFor(ThemeName(__2?.name,theme),theme));
        }
    }

    [HarmonyPatch(typeof(MusicInfomationController), "SetMusicData")]
    public static class InformationDataPatch
    {
        [HarmonyPrefix] public static void Prefix(MusicInfomationController __instance, MessageMusicData __0)
        {
            Information.Remove(__instance); Information.Add(__instance, new MusicIdentity { Id = __0?.MusicId ?? -1,
                Difficulty = __0?.MusicDifficultyID ?? -1, Utage = __0 != null && __0.MusicId >= 100000 });
            if (__0 != null || !Settings.Current.ExtraDifficulty) return;
            ApplyImage(Field<Image>(__instance, "difficultyImage"), null);
            ApplyImage(Field<Image>(__instance, "musicJacketBgImage"), null);
            PrepareNumbers(__instance, Master, false, Theme.None);
        }
    }
    [HarmonyPatch(typeof(MusicInfomationController), "SetDifficulty")]
    public static class InformationDifficultyPatch
    {
        [HarmonyPostfix] public static void Postfix(MusicInfomationController __instance, MusicDifficultyID difficulty, bool isUtage)
        {
            var theme = !isUtage ? ThemeFor(Identity(__instance), (int)difficulty) : Theme.None;
            var code = ThemeCode(theme);
            ApplyImage(Field<Image>(__instance, "difficultyImage"), SpriteFor("UI_UPE_MBase_" + code,theme));
            ApplyImage(Field<Image>(__instance, "musicJacketBgImage"), SpriteFor("UI_UPE_MusicJacket_Base_" + code,theme));
        }
    }
    [HarmonyPatch]
    public static class LevelPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MusicChainCardObejct), "SetLevel");
            yield return AccessTools.Method(typeof(ResultMonitor), "SetLevel");
            yield return AccessTools.Method(typeof(SingleResultCardController), "SetLevel");
            yield return AccessTools.Method(typeof(KOP_ResultTrackData), "SetDifficultyLevel");
            yield return AccessTools.Method(typeof(MusicInfomationController), "Setlevel");
            yield return AccessTools.Method(typeof(TrackStartMonitor), "SetTrackStart");
        }
        private static void Context(Component owner, MethodBase method, object[] args, out int difficulty, out bool utage)
        {
            var identity = Identity(owner); difficulty = identity.Difficulty; utage = identity.Utage;
            if (method.Name == "SetLevel") { difficulty = (int)args[2]; utage = (bool)args[3]; }
            else if (method.Name == "SetDifficultyLevel") { difficulty = (int)args[2]; }
            else if (method.Name == "Setlevel") { difficulty = (int)args[1]; utage = (bool)args[2]; }
        }
        [HarmonyPrefix] public static void Prefix(Component __instance, MethodBase __originalMethod, object[] __args, out Theme __state)
        {
            __state = levelTheme; Context(__instance, __originalMethod, __args, out var difficulty, out var utage);
            levelTheme = !utage ? ThemeFor(Identity(__instance), difficulty) : Theme.None;
            PrepareNumbers(__instance, difficulty, utage, levelTheme);
        }
        [HarmonyPostfix] public static void Postfix(Component __instance, MethodBase __originalMethod, object[] __args)
        {
            Context(__instance, __originalMethod, __args, out var difficulty, out var utage);
            PrepareNumbers(__instance, difficulty, utage, levelTheme);
            if (__instance is MonitorBase monitor) Scan(monitor);
            else if (__instance is KOP_ResultTrackData) ScanImages(__instance);
        }
        [HarmonyFinalizer] public static void Finalizer(Theme __state) => levelTheme = __state;
    }
    [HarmonyPatch(typeof(CommonPrefab), "GetMusicLevelSprites")]
    public static class CommonNumbersPatch
    {
        [HarmonyPrefix] public static bool Prefix(int difficulty, ref Sprite[] __result)
        {
            if (levelTheme == Theme.None || difficulty < 0 || difficulty > 4) return true;
            var sheet = NumberSheet(levelTheme); if (sheet == null) return true;
            __result = sheet; return false;
        }
    }
    [HarmonyPatch(typeof(MultipleImage), "ChangeSprite")]
    public static class MultiplePatch
    {
        [HarmonyPrefix] public static void Prefix(MultipleImage __instance, int index)
        { ApplySlot(__instance, ThemeFor(Identity(__instance), index), null, false, index); }
        [HarmonyPostfix] public static void Postfix(MultipleImage __instance)
        {
            // KOP history uses slot 0 for its standard-chart plate regardless
            // of difficulty. Reapply after native animation changes that slot.
            if (FindResultTrack(__instance) == null || __instance.MultiSprites == null ||
                __instance.MultiSprites.Count <= Master || __instance.MultiSprites[Master]?.name.Contains("KopMBase") != true) return;
            var identity = Identity(__instance);
            ApplySlot(__instance, ThemeFor(identity, identity.Difficulty), null, true);
        }
    }
    [HarmonyPatch]
    public static class ProcessStartPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TrackStartProcess), "OnStart");
            yield return AccessTools.Method(typeof(GameProcess), "OnStart");
            yield return AccessTools.Method(typeof(ResultProcess), "OnStart");
        }
        [HarmonyPostfix] public static void Postfix(object __instance)
        {
            var monitors = Field<MonitorBase[]>(__instance, "_monitors");
            if (monitors == null) return;
            foreach (var monitor in monitors)
            { if (monitor == null) continue; Scan(monitor); MelonCoroutines.Start(ScanLater(monitor, MonitorIdentity(monitor))); }
        }
    }
}
