using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Monitor;
using Manager;
[assembly:MelonInfo(typeof(TintProbe),"Tint load-time regression","1.0","Codex")]
[assembly:MelonGame("sega-interactive","Sinmai")]
[assembly:HarmonyDontPatchAll]

// Run in the disposable Unity harness, with SINMAI_PROBE_TARGET and
// SINMAI_PROBE_OUTPUT set to the mod DLL and result file respectively.
public class TintProbe : MelonMod
{
    const BindingFlags Flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    System.Type custom; Assembly asm; readonly StringBuilder report = new StringBuilder(); int frames; bool done;
    object Call(string name, params object[] args) => AccessTools.Method(custom, name).Invoke(null, args);
    void Set(object obj, string name, object value) => AccessTools.Field(obj.GetType(), name).SetValue(obj, value);
    IDictionary Cache => (IDictionary)custom.GetField("VisualTintSprites", Flags).GetValue(null);
    void Check(bool ok, string name) { if (!ok) throw new Exception(name); report.AppendLine("PASS " + name); }
    public override void OnUpdate()
    {
        if (done || ++frames < 30) return;
        done = true;
        try { Run(); report.Insert(0, "PASS\n"); } catch (Exception e) { report.Insert(0, "FAIL " + e + "\n"); }
        File.WriteAllText(Environment.GetEnvironmentVariable("SINMAI_PROBE_OUTPUT"), report.ToString());
        Application.Quit();
    }
    void Warm(Monitor.Game.GameCtrl ctrl, NotesReader reader)
    {
        for (int i = 0; i < 200; i++)
        {
            int before = Cache.Count;
            bool ready = (bool)Call("WarmHintTints", ctrl, reader);
            if (Cache.Count - before > 1) throw new Exception("More than one texture built in a loading step");
            if (ready) return;
        }
        throw new Exception("Warmup did not terminate");
    }
    void Run()
    {
        asm = Assembly.LoadFrom(Environment.GetEnvironmentVariable("SINMAI_PROBE_TARGET"));
        custom = asm.GetType("SinmaiAlpha.Notes.CustomNoteTypes");
        report.AppendLine("ASSEMBLY " + asm.Location + " MVID=" + asm.ManifestModule.ModuleVersionId);
        var tex = new Texture2D(256, 256, TextureFormat.RGBA32, false);
        tex.SetPixels32(Enumerable.Repeat(new Color32(230, 190, 30, 255), 256 * 256).ToArray()); tex.Apply(false, true);
        var sprite = Sprite.Create(tex, new Rect(0, 0, 256, 256), new Vector2(.5f, .5f), 100, 0, SpriteMeshType.FullRect);
        var line = new GameObject("each line").AddComponent<SpriteRenderer>(); line.sprite = sprite; line.color = new Color(1, 1, 1, .8f); line.flipX = true;
        var go = new GameObject("inactive controller"); go.SetActive(false); var ctrl = go.AddComponent<Monitor.Game.GameCtrl>();
        var gg = new GameObject("inactive guide"); gg.SetActive(false); var guide = gg.AddComponent<NoteGuide>();
        Set(guide, "_spriteEachRender", line); Set(ctrl, "_guideObjectList", new List<NoteGuide> { guide, guide });
        var reader = new NotesReader();
        var note = new NoteData { startButtonPos = 1, isEach = true };
        note.eachChild.Add(new NoteData { startButtonPos = 2 }); reader.GetNoteList().Add(note);
        var payload = Activator.CreateInstance(custom.GetNestedType("VisualPayload", Flags), true);
        var vn = Activator.CreateInstance(asm.GetType("SinmaiAlpha.ChartVisuals.VisualNote"));
        Set(vn, "Family", "hold"); Set(vn, "Each", true); Set(payload, "Note", vn); Set(payload, "Stream", "");
        var notes = custom.GetField("VisualNotes", Flags).GetValue(null);
        notes.GetType().GetMethod("Add").Invoke(notes, new[] { (object)note, payload });
        var timeline = Activator.CreateInstance(custom.GetNestedType("VisualTimeline", Flags), true);
        var streams = (IDictionary)timeline.GetType().GetField("Streams", Flags).GetValue(timeline);
        var ct = asm.GetType("SinmaiAlpha.ChartVisuals.VisualChange"); var vt = asm.GetType("SinmaiAlpha.ChartVisuals.VisualValue");
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ct));
        foreach (var rgb in new[] { 0xE07026, 0xE07026, 0x33AA66 })
        {
            var change = Activator.CreateInstance(ct); Set(change, "Kind", "color"); Set(change, "Type", ""); Set(change, "Time", 21650.94);
            var val = Activator.CreateInstance(vt); Set(val, "Color", (int?)rgb); Set(change, "Value", val); list.Add(change);
        }
        foreach (var type in new[] { "blockarea", "slidestar", "mine" })
        {
            var change = Activator.CreateInstance(ct); Set(change, "Kind", "color"); Set(change, "Type", type);
            var val = Activator.CreateInstance(vt); Set(val, "Color", (int?)0x123456); Set(change, "Value", val); list.Add(change);
        }
        streams[""] = list;
        var timelines = custom.GetField("VisualTimelines", Flags).GetValue(null);
        timelines.GetType().GetMethod("Add").Invoke(timelines, new[] { (object)reader, timeline });
        Warm(ctrl, reader);
        Check(Cache.Count == 2, "step budget, duplicate deduplication and unrelated type exclusion");
        var sw = Stopwatch.StartNew(); var cached = (Sprite)Call("GetVisualTintSprite", sprite, 0xE07026, line.color); sw.Stop();
        report.AppendLine("warmed gameplay lookup ms=" + sw.Elapsed.TotalMilliseconds.ToString("F3"));
        Check(Cache.Count == 2, "first COLORV uses prebuilt sprite");
        var part = Activator.CreateInstance(custom.GetNestedType("VisualPart", Flags), true); Set(part, "Native", line); Set(part, "Hint", true);
        var value = Activator.CreateInstance(vt); Set(value, "Color", (int?)0xE07026); Set(value, "Alpha", (float?).5f); Set(value, "X", (float?)3); Set(value, "Y", (float?)3);
        var material = line.sharedMaterial; Call("ApplyVisualPart", part, value, Activator.CreateInstance(vt));
        var output = line.GetComponentsInChildren<SpriteRenderer>().Single(x => x != line);
        Check(output.sprite == cached && output.sharedMaterial == material, "native arc material and warmed sprite retained");
        Check(output.flipX && output.transform.localScale == Vector3.one && Math.Abs(output.color.a - .4f) < .001f, "arc direction, geometry and opacity retained");
        Warm(ctrl, reader); Check(Cache.Count == 2, "completed queue is idempotent");
        var go2 = new GameObject("second controller"); go2.SetActive(false); var ctrl2 = go2.AddComponent<Monitor.Game.GameCtrl>();
        var gg2 = new GameObject("second guide"); gg2.SetActive(false); var guide2 = gg2.AddComponent<NoteGuide>();
        var line2 = new GameObject("second arc").AddComponent<SpriteRenderer>(); line2.sprite = sprite; line2.color = new Color(.7f, .9f, .5f, 1);
        Set(guide2, "_spriteEachRender", line2); Set(ctrl2, "_guideObjectList", new List<NoteGuide> { guide2 });
        Warm(ctrl2, reader); Check(Cache.Count == 4, "shared reader still warms each controller vertex color");
        Call("ClearVisualTintCache");
        var mineTextures = (IDictionary)custom.GetField("MineTextures", Flags).GetValue(null); mineTextures["mine"] = tex;
        Set(vn, "Mine", true); var baseline = AccessTools.Field(vn.GetType(), "Base").GetValue(vn); Set(baseline, "Color", (int?)0xE07026);
        Warm(ctrl, reader); Check(Cache.Count == 4, "mine arc and ordinary arc warm authored and mine-specific colors");
        Set(vn, "Mine", false); Set(baseline, "Color", null);
        Call("ClearVisualTintCache"); Check(Cache.Count == 0, "chart cleanup clears sprites");
        Check(custom.GetField("VisualPixelScratch", Flags).GetValue(null) == null, "chart cleanup releases pixel scratch");
        Warm(ctrl, new NotesReader()); Check(Cache.Count == 0, "ordinary chart creates no tint textures");
        Set(ctrl, "_guideObjectList", new List<NoteGuide>());
        Check(!(bool)Call("WarmHintTints", ctrl, reader), "collection delays ready result");
        Check(!(bool)Call("WarmHintTints", ctrl, reader), "missing pool retries without marking completed");
        Set(ctrl, "_guideObjectList", new List<NoteGuide> { guide }); Warm(ctrl, reader);
        Check(Cache.Count == 2, "ready pool completes retry");
        Call("ClearVisualTintCache"); custom.GetField("hintTintBuilt", Flags).SetValue(null, 32);
        Warm(ctrl, reader); Check(Cache.Count == 0, "global key budget finishes without building");
        Call("ClearVisualTintCache"); custom.GetField("hintTintBytes", Flags).SetValue(null, 128L * 1024 * 1024);
        Warm(ctrl, reader); Check(Cache.Count == 0, "global byte budget finishes without building");
        Call("ClearVisualTintCache"); Set(ctrl, "_guideObjectList", new List<NoteGuide>());
        Warm(ctrl, reader); Check(Cache.Count == 0, "permanently missing pool cannot trap loading");
        Call("ClearVisualTintCache"); Set(ctrl, "_guideObjectList", new List<NoteGuide> { guide });
        note.eachChild.Clear(); Warm(ctrl, reader); Check(Cache.Count == 0, "solo note without arc creates no tint texture");
        Call("ClearVisualTintCache");
    }
}
