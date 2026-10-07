using System;
using System.Collections.Generic;
using System.IO;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Notes.Libs;
using HarmonyLib;
using Manager;
using MAI2.Util;
using MelonLoader;
using Monitor;
using Monitor.Game;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private static readonly Dictionary<GameMonitor, NativeComboDisplay> ComboDisplays = new();

    private static void ApplyComboPresentation(GameMonitor monitor)
    {
        var reader = NotesManager.Instance(monitor.MonitorIndex).getReader();
        var main = Traverse.Create(monitor).Field("Main").GetValue<CanvasGroup>();
        if (GuiSizes.SinglePlayer && monitor.MonitorIndex != 0 || main == null ||
            !PresentationTimelines.TryGetValue(reader, out var timeline) || timeline.ComboDisplay == null)
        { ReleaseComboPresentation(monitor); return; }
        if (timeline.ComboDisplay.Evaluate(NotesManager.GetCurrentMsec()) == ComboDisplayCommands.PlayerDefault)
        { ReleaseComboPresentation(monitor); return; }
        if (!ComboDisplays.TryGetValue(monitor, out var display) || display == null)
            ComboDisplays[monitor] = display = monitor.gameObject.AddComponent<NativeComboDisplay>();
        var native = Traverse.Create(monitor).Field("GameDispCtrl").GetValue<GameObjectCtrl>();
        display.Bind(monitor, reader, main, native != null ? native.GetAchiveObj() : null, timeline.ComboDisplay);
    }
    private static void ReleaseComboPresentation(GameMonitor monitor)
    {
        if (!ComboDisplays.TryGetValue(monitor, out var display)) return;
        if (display != null) { display.Stop(); Object.Destroy(display); }
        ComboDisplays.Remove(monitor);
    }
    private static void ResetComboPresentation(NotesReader reader)
    {
        var stale = new List<GameMonitor>();
        foreach (var pair in ComboDisplays) if (pair.Value != null && pair.Value.Reader == reader) stale.Add(pair.Key);
        foreach (var monitor in stale) ReleaseComboPresentation(monitor);
    }
    private static void ReleaseComboPresentation()
    {
        foreach (var display in ComboDisplays.Values) if (display != null) { display.Stop(); Object.Destroy(display); }
        ComboDisplays.Clear();
    }
}

// Reuse Sinmai's center counter, native sprite atlases and Execute lifecycle.
// Only presentation is leased: score, life/course/shutter and user options remain native.
public sealed class NativeComboDisplay : MonoBehaviour
{
    private GameMonitor owner;
    private GameAchiveNum native;
    private ComboDisplayCommands.Track track;
    private DB.OptionCenterdisplayID originalMode;
    private int previousMode = int.MinValue;
    private sealed class NumberLease { public int Count; public Vector2 Size; }
    private readonly Dictionary<SpriteCounter, NumberLease> numberLeases = new();
    private void RestoreNumberFrames()
    {
        foreach (var pair in numberLeases)
        {
            if (pair.Key == null) continue;
            while (pair.Key.FrameList.Count > pair.Value.Count) pair.Key.RemoveFormatFrame();
            pair.Key.rectTransform.sizeDelta = pair.Value.Size;
        }
        numberLeases.Clear();
    }
    private readonly Dictionary<GameObject, bool> active = new();
    private readonly Dictionary<Transform, Vector3> positions = new();
    private readonly Dictionary<Graphic, Color> colors = new();
    private readonly Dictionary<UnityEngine.UI.Image, Sprite> sprites = new();
    internal NotesReader Reader { get; private set; }
    internal static readonly string[] CounterFields = {
        "_title", "_titleDxScore", "_intNum", "_intNumMain", "_floatNum", "_floatNumMain",
        "_floatNumDenomi", "_floatNumDenomiMain", "_achiveTiele"
    };
    private T Field<T>(string name) => Traverse.Create(native).Field(name).GetValue<T>();
    internal static DB.OptionCenterdisplayID NativeMode(int mode)
    {
        switch (mode)
        {
            case 1: return DB.OptionCenterdisplayID.Combo;
            case 3: case 11: return DB.OptionCenterdisplayID.AchivePlus;
            case 4: case 12: return DB.OptionCenterdisplayID.AchiveMinus1;
            case 13: return DB.OptionCenterdisplayID.DeluxScore;
            default: return DB.OptionCenterdisplayID.Off;
        }
    }
    internal void Bind(GameMonitor monitor, NotesReader reader, CanvasGroup group, GameAchiveNum counter, ComboDisplayCommands.Track changes)
    {
        if (Reader != reader || native != counter)
        {
            Stop(); native = counter;
            if (native != null)
            {
                originalMode = Field<DB.OptionCenterdisplayID>("_displayCenter");
                foreach (var name in CounterFields)
                {
                    var component=Field<Component>(name); if(component==null)continue;
                    active[component.gameObject]=component.gameObject.activeSelf;
                    positions[component.transform]=component.transform.localPosition;
                    foreach(var graphic in component.GetComponentsInChildren<Graphic>(true))
                    { colors[graphic]=graphic.color; if(graphic is UnityEngine.UI.Image image) sprites[image]=image.sprite; }
                }
            }
        }
        owner=monitor; Reader=reader; track=changes; enabled=true;
    }
    private void InvalidateNativeValue()
    {
        if(native==null)return;
        Traverse.Create(native).Field("_preData").SetValue(int.MinValue);
        Traverse.Create(native).Field("_preDxScoreData").SetValue(int.MinValue);
        Traverse.Create(native).Field("_dispDelayCount").SetValue(3);
    }
    internal void Stop()
    {
        enabled=false;
        RestoreNumberFrames();
        if(native!=null) { Traverse.Create(native).Field("_displayCenter").SetValue(originalMode); InvalidateNativeValue(); }
        foreach(var pair in active) if(pair.Key!=null)pair.Key.SetActive(pair.Value);
        foreach(var pair in positions) if(pair.Key!=null)pair.Key.localPosition=pair.Value;
        foreach(var pair in colors) if(pair.Key!=null) { if(pair.Key is SpriteCounter counter)counter.SetColor(pair.Value); else pair.Key.color=pair.Value; }
        foreach(var pair in sprites) if(pair.Key!=null)pair.Key.sprite=pair.Value;
        active.Clear(); positions.Clear(); colors.Clear(); sprites.Clear();
        native=null; owner=null; Reader=null; track=null; previousMode=int.MinValue;
    }
    private void OnDestroy()=>Stop();
    private void Show(string field,bool visible)
    { var root=Field<Component>(field); if(root!=null&&root.gameObject.activeSelf!=visible)root.gameObject.SetActive(visible); }
    private void Text(string field,string value)
    {
        var counter=Field<SpriteCounter>(field);
        if(counter==null)return;
        // Clear unused digits on every update. Long classic scores can exceed
        // the original combo counter width, so extend its native digit frames.
        if(value.Length>counter.FrameList.Count)
        {
            if(!numberLeases.ContainsKey(counter)) numberLeases[counter]=new NumberLease { Count=counter.FrameList.Count, Size=counter.rectTransform.sizeDelta };
            while(counter.FrameList.Count<value.Length)
            {
                var count=counter.FrameList.Count;
                counter.AddFormatFream();
                if(counter.FrameList.Count==count)break;
                if(count>0)
                {
                    var previous=counter.FrameList[count-1]; var added=counter.FrameList[count];
                    added.Scale=previous.Scale; added.AnimationScale=previous.AnimationScale;
                    if(counter.BetweenList.Count>1) counter.BetweenList[counter.BetweenList.Count-1].Size=counter.BetweenList[counter.BetweenList.Count-2].Size;
                }
            }
        }
        value=value.PadLeft(counter.FrameList.Count);
        if(Traverse.Create(counter).Field("mainText").GetValue<string>()!=value)counter.ChangeText(value);
    }
    private void SetLayout(int mode)
    {
        RestoreNumberFrames();
        foreach(var pair in positions) if(pair.Key!=null)pair.Key.localPosition=pair.Value;
        Traverse.Create(native).Field("_animCount").SetValue(0);
        var mapped=NativeMode(mode);
        Traverse.Create(native).Field("_displayCenter").SetValue(mapped);
        InvalidateNativeValue();
        var rate=mode==3||mode==4||mode==11||mode==12;
        foreach(var name in CounterFields) Show(name,false);
        if(mode==0)return;
        Show("_title",mapped!=DB.OptionCenterdisplayID.Off);
        var title=Field<UI.MultipleImage>("_title");
        if(title!=null&&mapped!=DB.OptionCenterdisplayID.Off)title.ChangeSprite((int)mapped);
        Show("_intNum",!rate); Show("_intNumMain",!rate);
        Show("_floatNum",rate); Show("_floatNumMain",rate);
        Show("_floatNumDenomi",rate); Show("_floatNumDenomiMain",rate); Show("_achiveTiele",rate);
        var table=CommonScriptable.GetColorSetting();
        if(table!=null)
        {
            var palette=table.GameCenterNumColor;
            if(palette!=null&&(int)mapped<palette.Length)
                foreach(var field in new[]{"_intNumMain","_floatNumMain","_floatNumDenomiMain"}) Field<SpriteCounter>(field)?.SetColor(palette[(int)mapped]);
        }
    }
    private void LateUpdate()
    {
        if(!enabled||owner==null||native==null||track==null)return;
        var mode=track.Evaluate(NotesManager.GetCurrentMsec());
        if(mode==ComboDisplayCommands.PlayerDefault) { Stop(); return; }
        if(mode!=previousMode) { SetLayout(mode); previousMode=mode; }
        if(mode==0) { foreach(var name in CounterFields) Show(name,false); return; }
        // Classic score/rate modes have no native settings enum. Their values
        // use the existing read-only native score adapter and native number sprites.
        if(mode==2||mode==3||mode==4||mode==101||mode==102)
        {
            var manager=Singleton<GamePlayManager>.Instance;
            var data=NativeComboValues.CaptureSession(manager.GetGameScore(owner.MonitorIndex),manager.GetGameScore(1-owner.MonitorIndex));
            if(mode==3||mode==4)
            {
                var rate=data.Rate(mode).ToString("0.0000",System.Globalization.CultureInfo.InvariantCulture).Split('.');
                Text("_floatNum",rate[0].PadLeft(3)); Text("_floatNumMain",rate[0].PadLeft(3));
                Text("_floatNumDenomi","."+rate[1]+"%"); Text("_floatNumDenomiMain","."+rate[1]+"%");
            }
            else
            {
                var value=(mode==2?data.Score:mode==101?data.NormalizedScore:data.NormalizedDownScore).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Text("_intNum",value); Text("_intNumMain",value);
            }
        }
    }
}
