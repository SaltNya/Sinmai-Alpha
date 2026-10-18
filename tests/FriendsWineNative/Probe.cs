using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;
using HarmonyLib;
using Manager;
using MelonLoader;
using UnityEngine;
using Type = System.Type;

[assembly: MelonInfo(typeof(FriendsWineProbe), "Friends wine reader regression", "1.0", "Codex")]
[assembly: MelonGame("sega-interactive", "Sinmai")]
[assembly: HarmonyDontPatchAll]
public sealed class FriendsWineProbe : MelonMod
{
    int frames; bool done; Type custom; Assembly candidate;
    readonly StringBuilder report = new StringBuilder();
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static object Get(object o, string n) => AccessTools.Field(o.GetType(), n).GetValue(o);
    static void Set(object o, string n, object v) => AccessTools.Field(o.GetType(), n).SetValue(o, v);
    static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    public override void OnUpdate()
    {
        if (done || ++frames < 30) return;
        done = true;
        try { Run(); report.AppendLine("PASS ALL"); } catch (Exception e) { report.AppendLine("FAIL " + e); }
        File.WriteAllText(Environment.GetEnvironmentVariable("SINMAI_PROBE_OUTPUT"), report.ToString());
        Application.Quit();
    }
    void Apply(HarmonyLib.Harmony harmony, Type type)
    {
        AccessTools.DeclaredMethod(type, "OnBeforePatch")?.Invoke(null, null);
        harmony.PatchAll(type);
        AccessTools.DeclaredMethod(type, "OnAfterPatch")?.Invoke(null, null);
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
            if (nested.GetCustomAttributes(false).Length != 0) Apply(harmony, nested);
    }
    void Run()
    {
        Assembly.LoadFrom(Path.Combine(Environment.CurrentDirectory, "UserLibs", "System.Numerics.dll"));
        candidate = Assembly.LoadFrom(Environment.GetEnvironmentVariable("SINMAI_PROBE_TARGET"));
        custom = candidate.GetType("SinmaiAlpha.Notes.CustomNoteTypes", true);
        report.AppendLine("Mod MVID=" + candidate.ManifestModule.ModuleVersionId);
        var scoreManager = MAI2.Util.Singleton<GamePlayManager>.Instance;
        var log = Activator.CreateInstance(typeof(GamePlayManager).GetNestedType("ScoreLogClass", F), true);
        ((IList)Get(scoreManager, "_scoreLists")).Add(log);
        foreach (var score in (GameScoreList[])Get(log, "_score")) score.UserOption = new Manager.UserDatas.UserOption();
        AccessTools.Field(typeof(GameManager), "MusicTrackNumber").SetValue(null, 1u);
        var managers = new[] { (NotesManager)FormatterServices.GetUninitializedObject(typeof(NotesManager)), (NotesManager)FormatterServices.GetUninitializedObject(typeof(NotesManager)) };
        AccessTools.Field(typeof(NotesManager), "_instance").SetValue(null, managers);
        var patches = new HarmonyLib.Harmony("friends.wine.production");
        Apply(patches, custom);
        var fixture = new HarmonyLib.Harmony("friends.wine.score.fixture");
        fixture.Patch(AccessTools.Method(typeof(GameScoreList), "Initialize"), prefix: new HarmonyMethod(typeof(FriendsWineProbe), nameof(AllocateResults)));
        foreach (var chart in Environment.GetEnvironmentVariable("SINMAI_PROBE_CHARTS").Split(';'))
        {
            var reader = new NotesReader(); Set(managers[0], "_notesReader", reader);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var loaded = reader.load(chart, LoadType.LOAD_FULL);
            var notes = reader.GetNoteList();
            var holes = notes.Where((n, i) => n.indexNote != i).Count();
            report.AppendLine(Path.GetFileName(chart) + " load=" + loaded + " notes=" + notes.Count + " indexHoles=" + holes + " loadMs=" + watch.ElapsedMilliseconds);
            Check(loaded, "Reader rejected valid chart");
            // Execute the actual crashing loop. Only account/setup I/O is
            // replaced; AllocateResults mirrors the native allocation formula.
            AccessTools.Method(typeof(GamePlayManager), "CreateResult").Invoke(scoreManager, new object[] { 0, false });
            report.AppendLine("PASS native CreateResult loop and GetScoreAt bounds");
            Check(holes == 0, "Reader left non-contiguous score indices");
            var store = AccessTools.Field(custom, "RuntimeCharts").GetValue(null);
            var state = AccessTools.Method(store.GetType(), "Reader").Invoke(store, new object[] { reader, false });
            var targets = (IDictionary)Get(state, "SvScrollPosByNoteIndex");
            var streams = (IDictionary)Get(state, "StreamTypeByNoteIndex");
            var groups = new Dictionary<string, List<float>>();
            foreach (NoteData note in notes)
            {
                if (!(bool)AccessTools.Method(custom, "IsFakeNote").Invoke(null, new object[] { note }) || !streams.Contains(note.indexNote) || !targets.Contains(note.indexNote)) continue;
                var stream = (string)streams[note.indexNote];
                if (!groups.TryGetValue(stream, out var list)) groups[stream] = list = new List<float>();
                list.Add((float)targets[note.indexNote]);
            }
            foreach (var pair in groups.Where(p => p.Value.Count >= 500))
            {
                report.AppendLine("Dense " + pair.Key + " notes=" + pair.Value.Count + " distinctTargets=" + pair.Value.Distinct().Count());
                Check(pair.Value.Distinct().Count() >= pair.Value.Count * .95, "Dense visual targets collapsed");
            }
            if (Environment.GetEnvironmentVariable("SINMAI_PROBE_LATE_HOLDS") == "1")
            {
                var scope = AccessTools.Method(store.GetType(), "Enter").Invoke(store, new[] { state });
                try
                {
                    foreach (var n in notes.Where(n => n.type.isHold() && n.time.msec > 170000 && streams.Contains(n.indexNote)))
                    {
                        float visible = (float)AccessTools.Method(custom, "GetRingFirstVisibleTimeCached").Invoke(null, new object[] { n, 600f });
                        float lead = (float)AccessTools.Method(custom, "SvScaleActivationLead").Invoke(null, new object[] { 1200f, 2f, n });
                        report.AppendLine("LATE HOLD index=" + n.indexNote + " stream=" + streams[n.indexNote] + " head=" + n.time.msec + " visible=" + visible + " lead=" + lead + " missingMs=" + (n.time.msec - lead - visible));
                        if (Environment.GetEnvironmentVariable("SINMAI_PROBE_EXPECT_FIXED") == "1")
                            Check(n.time.msec - lead <= visible + .02f, "Long Hold activation truncated");
                    }
                }
                finally { AccessTools.Method(store.GetType(), "Exit").Invoke(store, new[] { scope }); }
                BrightnessCases(reader);
                if (Environment.GetEnvironmentVariable("SINMAI_PROBE_EXPECT_FIXED") == "1")
                    PoolAndHoldCases(managers[0], store, state);
                continue;
            }
            if (groups.Any(p => p.Value.Count >= 500) || Environment.GetEnvironmentVariable("SINMAI_PROBE_FAMILIES") == "1") PoolAndHoldCases(managers[0], store, state);
        }
        fixture.UnpatchSelf(); patches.UnpatchSelf();
    }
    void PoolAndHoldCases(NotesManager manager, object store, object state)
    {
        GameNotePrefabContainer.Initialize();
        var monitorPrefab = Resources.Load<GameObject>("Process/Game/GameProcess").GetComponent<Monitor.GameMonitor>();
        var controllerPrefab = (GameObject)Get(monitorPrefab, "GameContrullerPrefub");
        foreach (var mask in controllerPrefab.GetComponentsInChildren<SpriteMask>(true))
            report.AppendLine("MASK " + mask.name + " custom=" + mask.isCustomRangeActive + " back=" + mask.backSortingOrder + ":" + mask.backSortingLayerID + " front=" + mask.frontSortingOrder + ":" + mask.frontSortingLayerID);
        var root = new GameObject("Owned visual pool regression"); root.SetActive(false);
        var controller = root.AddComponent<Monitor.Game.GameCtrl>();
        Set(controller, "monitorIndex", 0); Set(controller, "apperMsecTap", 1200f);
        foreach (var name in new[] { "tap", "star", "hold", "break", "breakStar", "breakHold", "guide" })
        {
            var field = AccessTools.Field(controller.GetType(), "_" + name + "ObjectList");
            var list = (IList)Activator.CreateInstance(field.FieldType);
            var prefab = typeof(GameNotePrefabContainer).GetProperty(char.ToUpperInvariant(name[0]) + name.Substring(1)).GetValue(null, null);
            Check(prefab != null, "Missing native prefab " + name); list.Add(prefab); field.SetValue(controller, list);
            var parent = new GameObject(name); parent.transform.SetParent(root.transform, false);
            Set(controller, "_" + name + "ListParent", parent);
        }
        var minePools = new Dictionary<string, object>();
        foreach (var name in new[] { "tap", "star", "hold", "break", "breakStar", "breakHold" })
        {
            var typeName = name == "tap" ? "MineTapNote" : name == "star" ? "MineStarNote" : name == "hold" ? "MineHoldNote" : name == "break" ? "MineBreakNote" : name == "breakStar" ? "MineBreakStarNote" : "MineBreakHoldNote";
            var field = AccessTools.Field(controller.GetType(), "_" + name + "ObjectList");
            var list = (IList)Activator.CreateInstance(field.FieldType);
            var prefab = typeof(GameNotePrefabContainer).GetProperty(char.ToUpperInvariant(name[0]) + name.Substring(1)).GetValue(null, null);
            var parent = ((GameObject)Get(controller, "_" + name + "ListParent")).transform;
            var create = AccessTools.Method(candidate.GetType("SinmaiAlpha.Notes." + typeName), "CreateFrom");
            int initial = name == "tap" ? 192 : 64;
            for (int i = 0; i < initial; i++)
            {
                var instance = (Monitor.NoteBase)create.Invoke(null, new object[] { prefab, parent });
                instance.gameObject.SetActive(false); instance.ParentTransform = parent; list.Add(instance);
            }
            minePools[field.Name] = list;
        }
        ((IDictionary)AccessTools.Field(custom, "MinePools").GetValue(null))[controller] = minePools;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        controller.SetNoteManager(manager);
        var taps = (IList)Get(controller, "_tapObjectList"); var guides = (IList)Get(controller, "_guideObjectList");
        report.AppendLine("Native prefab prewarm: taps=" + taps.Count + " guides=" + guides.Count + " ms=" + watch.ElapsedMilliseconds);
        if (Environment.GetEnvironmentVariable("SINMAI_PROBE_FAMILIES") != "1")
            Check(taps.Count >= 601 && guides.Count >= 601, "Visual burst was not prewarmed");
        foreach (var entry in minePools) report.AppendLine("MINE POOL " + entry.Key + "=" + ((IList)entry.Value).Count);
        var size = taps.Count; controller.SetNoteManager(manager);
        Check(taps.Count == size, "Repeated manager binding grew pool again");
        Check(taps.Cast<Component>().Skip(1).All(n => !n.gameObject.activeSelf), "Prewarm activated notes");
        if (Environment.GetEnvironmentVariable("SINMAI_PROBE_DENSE_RENDER") == "1")
            DisplayCases(root, controller, manager, state);
        var scope = AccessTools.Method(store.GetType(), "Enter").Invoke(store, new[] { state });
        try
        {
            const int index = 999999;
            ((IDictionary)Get(state, "SvScrollPosByNoteIndex"))[index] = 20000f;
            ((IDictionary)Get(state, "SvTailScrollPosByNoteIndex"))[index] = 20000f;
            ((IDictionary)Get(state, "SvTypeByNoteIndex"))[index] = "s9999";
            ((IDictionary)Get(state, "HsMultByNoteIndex"))[index] = 2f;
            var hold = UnityEngine.Object.Instantiate(GameNotePrefabContainer.Hold, root.transform);
            Set(hold, "EndFlag", false); Set(hold, "NoteIndex", index); Set(hold, "AppearMsec", 1000f);
            Set(hold, "TailMsec", 2000f); Set(hold, "DefaultMsec", 1000f);
            AccessTools.Field(typeof(NotesManager), "_curMSec").SetValue(null, 1100f);
            var args = new object[] { hold, false, null };
            Check((bool)AccessTools.Method(custom, "TryRingPresentation").Invoke(null, args), "Hold presentation unavailable");
            Check((bool)Get(args[2], "Running") && (float)Get(args[2], "Scale") == 1f, "Reverse/frozen Hold head disappeared after judgment time");
            report.AppendLine("PASS native prefab pool sizing/rebind and held head pinning under reverse scroll");
        }
        finally { AccessTools.Method(store.GetType(), "Exit").Invoke(store, new[] { scope }); UnityEngine.Object.DestroyImmediate(root); }
    }
    void DisplayCases(GameObject root, Monitor.Game.GameCtrl controller, NotesManager manager, object state)
    {
        Process.GameNoteImageContainer.Initialize();
        GameParticleContainer.Initialize();
        Set(controller, "_activeNoteList", new List<Monitor.NoteBase>());
        Set(controller, "_judgeGradeObjectList", Enumerable.Repeat<Monitor.JudgeGrade>(null, 8).ToList());
        Set(controller, "_touchEffectObjectList", Enumerable.Repeat<Monitor.TouchEffect>(null, 8).ToList());
        var lanes = new List<GameObject>();
        for (int i = 0; i < 8; i++)
        {
            var lane = new GameObject("Lane " + i); lane.transform.SetParent(root.transform, false);
            lane.transform.localRotation = Quaternion.Euler(0, 0, -22.5f - 45f * i); lanes.Add(lane);
        }
        Set(controller, "_launcherObjectList", lanes);
        AccessTools.Field(controller.GetType(), "_noteStartPos").SetValue(null, 122.5f);
        AccessTools.Field(controller.GetType(), "_noteEndPos").SetValue(null, 480f);
        root.SetActive(true);
        if (Environment.GetEnvironmentVariable("SINMAI_PROBE_GUIDE_ALPHA") == "1")
        {
            var store = AccessTools.Field(custom, "RuntimeCharts").GetValue(null);
            var scope = AccessTools.Method(store.GetType(), "Enter").Invoke(store, new[] { state });
            try { GuideAlphaCases(controller, manager.getReader()); }
            finally { AccessTools.Method(store.GetType(), "Exit").Invoke(store, new[] { scope }); }
            root.SetActive(false); return;
        }
        if (Environment.GetEnvironmentVariable("SINMAI_PROBE_FAMILIES") == "1")
        {
            var kindMethod = AccessTools.Method(custom, "GetNoteKind");
            foreach (var family in manager.getReader().GetNoteList().Where(n => !n.type.isAllSlide() && !n.type.isTouch())
                .GroupBy(n => n.type.getEnum() + "/" + kindMethod.Invoke(null, new object[] { n })))
            {
                var registered = family.Count(n => controller.RegistNote(n));
                var live = (List<Monitor.NoteBase>)Get(controller, "_activeNoteList");
                report.AppendLine("FAMILY " + family.Key + " registered=" + registered + "/" + family.Count());
                Check(registered == family.Count(), "Family capacity exhausted");
                Check(live.Select(n => Get(n, "GuideObj")).Distinct().Count() == registered, "Shared/stale guide lease");
                foreach (var owner in live)
                {
                    ((Monitor.NoteGuide)Get(owner, "GuideObj"))?.ReturnToBase();
                    owner.gameObject.SetActive(false);
                }
                live.Clear();
            }
            root.SetActive(false); return;
        }
        var streams = (IDictionary)Get(state, "StreamTypeByNoteIndex");
        foreach (var group in new[] { "s3", "s42", "s48" })
        {
            var notes = manager.getReader().GetNoteList().Where(n => streams.Contains(n.indexNote) && (string)streams[n.indexNote] == group).ToArray();
            if (Environment.GetEnvironmentVariable("SINMAI_PROBE_STRESS") == "1" && group == "s48")
                notes = Enumerable.Range(0, 2048).Select(i => notes[i % notes.Length]).ToArray();
            int registered = 0;
            var now = notes[0].time.msec - 1800f;
            AccessTools.Field(typeof(NotesManager), "_curMSec").SetValue(null, now);
            foreach (var n in notes) if (controller.RegistNote(n)) registered++;
            var active = (List<Monitor.NoteBase>)Get(controller, "_activeNoteList");
            AccessTools.Method(custom, "BeforeVisualFrame").Invoke(null, new object[] { controller });
            foreach (var owner in active.ToArray()) owner.Execute();
            AccessTools.Method(custom, "AfterVisualFrame").Invoke(null, new object[] { controller });
            var allRenders = active.SelectMany(n => n.GetComponentsInChildren<SpriteRenderer>(true)).Where(r => r.enabled && r.gameObject.activeInHierarchy && r.sprite != null).ToArray();
            report.AppendLine("ALL renders=" + allRenders.Length + " first=" + allRenders[0].name + " bounds=" + allRenders[0].bounds + " material=" + allRenders[0].sharedMaterial.name + " mask=" + allRenders[0].maskInteraction + " layer=" + allRenders[0].gameObject.layer);
            var renders = active.Select(n => (SpriteRenderer)Get(n, "SpriteRender")).ToArray();
            report.AppendLine("DISPLAY " + group + " registered=" + registered + "/" + notes.Length +
                " active=" + active.Count + " enabled=" + renders.Count(r => r.enabled && r.gameObject.activeInHierarchy) +
                " scale=" + renders.Count(r => r.transform.lossyScale.x > .01f) + " alpha=" + renders.Count(r => r.color.a > .01f) +
                " sprite=" + renders.Count(r => r.sprite != null) + " position=" + renders[0].transform.position +
                " order=" + renders[0].sortingOrder + " tapPool=" + ((IList)Get(controller, "_tapObjectList")).Count + " guides=" + ((IList)Get(controller, "_guideObjectList")).Count);
            Check(registered == notes.Length, "Registration exhausted at " + registered + "/" + notes.Length);
            var cameraRoot = new GameObject("Owned render camera"); var camera = cameraRoot.AddComponent<Camera>();
            camera.enabled = false; camera.orthographic = true; camera.orthographicSize = 600; camera.aspect = 1;
            camera.transform.position = new Vector3(0, 0, -100); camera.farClipPlane = 1000;
            camera.cullingMask = -1;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var target = new RenderTexture(512, 512, 24); target.Create(); camera.targetTexture = target;
            foreach (var r in allRenders) r.maskInteraction = SpriteMaskInteraction.None;
            camera.Render();
            var previous = RenderTexture.active; RenderTexture.active = target;
            var texture = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, 512, 512), 0, 0); texture.Apply(); RenderTexture.active = previous;
            var pixels = texture.GetPixels32().Count(p => p.r > 20 || p.g > 20 || p.b > 20);
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SINMAI_PROBE_OUTPUT")), group + ".png"), ImageConversion.EncodeToPNG(texture));
            report.AppendLine("DISPLAY " + group + " rendered pixels=" + pixels);
            UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(cameraRoot);
            foreach (var owner in active)
            {
                var guide = (Monitor.NoteGuide)Get(owner, "GuideObj");
                if (guide != null) guide.ReturnToBase();
                owner.gameObject.SetActive(false);
            }
            active.Clear();
        }
        root.SetActive(false);
    }
    void GuideAlphaCases(Monitor.Game.GameCtrl controller, NotesReader reader)
    {
        var note = reader.GetNoteList().First(n => n.type.getEnum().ToString() == "Tap");
        var table = AccessTools.Field(custom, "VisualNotes").GetValue(null);
        var visualType = candidate.GetType("SinmaiAlpha.ChartVisuals.VisualNote");
        var payloadType = custom.GetNestedType("VisualPayload", F);
        var initializer = custom.GetNestedType("VisualInitializePatch");
        var before = AccessTools.Method(custom, "BeforeVisualFrame");
        var after = AccessTools.Method(custom, "AfterVisualFrame");
        void Payload(NoteData n, string alpha)
        {
            var payload = Activator.CreateInstance(payloadType, true);
            Set(payload, "Note", AccessTools.Method(visualType, "Decode").Invoke(null,
                new object[] { "VS|tap|0|E07026|2,3|" + alpha + "|-|-|-" }));
            Set(payload, "Stream", "s9999");
            table.GetType().GetMethod("Remove").Invoke(table, new object[] { n });
            table.GetType().GetMethod("Add").Invoke(table, new object[] { n, payload });
        }
        void Rebind(Monitor.NoteBase n, NoteData data)
        {
            AccessTools.Method(initializer, "Prefix").Invoke(null, new object[] { n });
            AccessTools.Method(initializer, "Postfix").Invoke(null, new object[] { n, new object[] { data } });
        }
        SpriteRenderer Effective(SpriteRenderer native)
        {
            if (native.enabled) return native;
            return native.GetComponentsInChildren<SpriteRenderer>(true).Single(r => r.name == "AquaMai Note Visual" && r.transform.parent == native.transform && r.enabled && r.gameObject.activeInHierarchy);
        }
        AccessTools.Method(custom, "ResetVisualCommands").Invoke(null, new object[] { reader });
        Payload(note, "0.5");
        AccessTools.Field(typeof(NotesManager), "_curMSec").SetValue(null, note.time.msec - 600f);
        Check(controller.RegistNote(note), "Could not register guide fixture");
        var owner = ((List<Monitor.NoteBase>)Get(controller, "_activeNoteList")).Single();
        var guide = (Monitor.NoteGuide)Get(owner, "GuideObj");
        guide.Initialize(90, 0);
        var radial = (SpriteRenderer)Get(guide, "_spriteRender");
        var each = (SpriteRenderer)Get(guide, "_spriteEachRender");
        Check(radial.sprite != null && each.sprite != null, "Missing native guide sprites");
        int checks = 0;
        void Frame(float alpha)
        {
            before.Invoke(null, new object[] { controller });
            guide.SetAlpha(.4f); each.color = new Color(1, 1, 1, .7f);
            after.Invoke(null, new object[] { controller });
            var displayed = Effective(radial);
            Check(Math.Abs(displayed.color.a - .4f * alpha) < .0001f,
                "Radial guide alpha expected=" + (.4f * alpha) + " actual=" + displayed.color.a);
            Check(Math.Abs(Effective(each).color.a - .7f * alpha) < .0001f, "Each guide alpha mismatch");
            Check(displayed.sprite == radial.sprite && displayed.color.r == 1 && displayed.color.g == 1 && displayed.color.b == 1,
                "Radial guide color changed");
            Check(displayed == radial || displayed.transform.localScale == Vector3.one, "Guide inherited note size");
            Check(radial.color.a == .4f, "Native guide fade was overwritten");
            checks++;
        }
        foreach (var alpha in new[] { .5f, 0f, 1f })
        {
            Payload(note, alpha.ToString(System.Globalization.CultureInfo.InvariantCulture)); Rebind(owner, note);
            for (int repeat = 0; repeat < 20; repeat++) Frame(alpha);
        }
        Payload(note, "0.5"); Rebind(owner, note);
        foreach (var row in new[] { "ALPHAV\t0\t0\ts9999~0.2", "ALPHAV\t1\t0\ts9999~0", "ALPHAV\t2\t0\ts9999~NULL" })
            AccessTools.Method(custom, "ReadVisualCommand").Invoke(null, new object[] { row });
        AccessTools.Method(custom, "BuildVisualCommands").Invoke(null, new object[] { reader });
        for (int bar = 0; bar < 3; bar++)
        {
            var t = new NotesTime(); t.init(bar, 0, reader);
            AccessTools.Field(typeof(NotesManager), "_curMSec").SetValue(null, t.msec + 1f);
            for (int repeat = 0; repeat < 20; repeat++) Frame(bar == 0 ? .2f : bar == 1 ? 0f : .5f);
        }
        report.AppendLine("PASS radial/Each guide ALPHA, ALPHAV, NULL, native fade, color/size isolation and no accumulation: " + checks);
        guide.ReturnToBase();
        Check(radial.enabled && each.enabled, "Pooled guide retained disabled native renderers");
        Check(radial.GetComponentsInChildren<SpriteRenderer>(true).Where(r => r.name == "AquaMai Note Visual").All(r => !r.gameObject.activeSelf), "Pooled guide overlay still active");
        // Transfer this guide while the old note still owns its visual lease.
        var second = reader.GetNoteList().First(n => n != note && n.type.getEnum().ToString() == "Tap");
        Payload(second, "0.8");
        Check(controller.RegistNote(second), "Could not register second guide fixture");
        var secondOwner = ((List<Monitor.NoteBase>)Get(controller, "_activeNoteList")).Last();
        var secondGuide = (Monitor.NoteGuide)Get(secondOwner, "GuideObj");
        if (secondGuide != guide) secondGuide.ReturnToBase();
        secondOwner.SetGuideObject(guide); guide.transform.SetParent(secondOwner.transform, false);
        guide.Initialize(90, 0); Rebind(secondOwner, second);
        AccessTools.Method(custom, "ResetVisualCommands").Invoke(null, new object[] { reader });
        Frame(.8f);
        AccessTools.Method(initializer, "Prefix").Invoke(null, new object[] { owner });
        Check(!radial.enabled && Math.Abs(Effective(radial).color.a - .32f) < .0001f, "Old lease restored the reassigned guide");
        guide.ReturnToBase(); guide.Initialize(0, 0);
        Check(radial.enabled && !each.gameObject.activeSelf, "Ordinary reused guide retained alpha/Each state");
        report.AppendLine("PASS native ReturnToBase/Initialize and guide transfer between live leases");
        AccessTools.Method(custom, "CollectVisualNotes").Invoke(null, new object[] { controller });
    }
    void BrightnessCases(NotesReader reader)
    {
        AccessTools.Method(custom, "ResetPresentationCommands").Invoke(null, new object[] { reader });
        var read = AccessTools.Method(custom, "ReadPresentationCommand");
        foreach (var row in new[] { "INNERBRIGHTNESS\t0\t0\t0,0,0,0,0", "INNERBRIGHTNESS\t1\t0\t1,1,0,0,0", "INNERBRIGHTNESS\t2\t0\t0.5,0,0,0,0", "INNERBRIGHTNESS\t3\t0\t-1,1,0,0,0" })
            read.Invoke(null, new object[] { row });
        AccessTools.Method(custom, "BuildPresentationCommands").Invoke(null, new object[] { reader });
        float Time(int bar) { var t = new NotesTime(); t.init(bar, 0, reader); return t.msec; }
        var postfix = AccessTools.Method(custom.GetNestedType("PresentationMovieBrightnessPatch"), "Postfix");
        var root = new GameObject("Owned brightness fixture"); var renderer = root.AddComponent<SpriteRenderer>();
        int checks = 0;
        try
        {
            foreach (var native in new[] { 0f, .35f, .85f })
            foreach (var sample in new[] {
                (Time(0)-1, native), (Time(0)+1, 0f), (Time(1)+500, .5f), (Time(1)+1100, 1f),
                (Time(2)+1, .5f), (Time(3)+500, (.5f+native)*.5f), (Time(3)+1100, native) })
            for (int repeat = 0; repeat < 20; repeat++)
            {
                AccessTools.Field(typeof(NotesManager), "_curMSec").SetValue(null, sample.Item1);
                renderer.color = new Color(.1f,.2f,.3f,native); // native UpdateMovie refreshes this each frame
                postfix.Invoke(null, new object[] { 0, renderer });
                Check(Math.Abs(renderer.color.a - sample.Item2) < .0001f,
                    "Brightness mismatch at " + sample.Item1 + " native=" + native + " expected=" + sample.Item2 + " actual=" + renderer.color.a);
                Check(renderer.color.r == .1f && renderer.color.g == .2f && renderer.color.b == .3f, "Brightness changed native color");
                checks++;
            }
            report.AppendLine("PASS absolute brightness 0/0.5/1, transitions, Default and no accumulation: " + checks);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    static bool AllocateResults(GameScoreList __instance, int monitorIndex)
    {
        var reader = NotesManager.Instance(monitorIndex).getReader();
        Set(__instance, "_judgeResultList", new JudgeResultSt[reader.GetTotal().GetAllNoteNumWithSpechialNotes()]);
        return false;
    }
}
