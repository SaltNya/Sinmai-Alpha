using SlideTable = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideTable;
using SlideJudgeProfile = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideJudgeProfile;
using SlideDefinition = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideDefinition;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DB;
using Manager;
using SinmaiAlpha.Notes.Libs;

namespace SlideJudgePrototype;

static class PrototypeProgram
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    static int Main(string[] args)
    {
        try
        {
            object tests = PrototypeTests.Run();
            Console.WriteLine("PASS prototype invariants " + JsonSerializer.Serialize(tests));
            object contours = ContourTests.Run();
            Console.WriteLine("PASS contour parity " + JsonSerializer.Serialize(contours));
            object fans = ContourTests.FanTests();
            Console.WriteLine("PASS fan input policy " + JsonSerializer.Serialize(fans));
            string output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/slide-judge-prototype");
            Directory.CreateDirectory(output);
            var rows = new List<Comparison>();
            foreach (var model in new[] { RegionModels.Seeds, RegionModels.Diameter, RegionModels.Contours })
            {
                foreach (var reference in SlideManager.Cases)
                {
                    string code = reference.GetProperty("code").GetString()!;
                    var definition = SlideDefinition.Parse(code);
                    rows.Add(Compare(code, definition.Paths[0], definition.JudgeProfile, model, reference));
                }
                foreach (var code in new[] { "1A3B6A8K4", "1Q99K5", "1^B5K7", "1K2", "1K1",
                    "D8D2K4", "E8E2K4", "DG2:1Q9K3", "TG1:1>B2>C", "TG1:E8^E2", "DF1:1w5", "DF1:1dw5" })
                {
                    var definition = SlideDefinition.Parse(code);
                    for (int lane = 0; lane < definition.Paths.Count; lane++)
                        rows.Add(Compare(code + (definition.IsFan ? $" · lane {lane + 1}" : ""), definition.Paths[lane], definition.JudgeProfile, model, null));
                }
            }
            var summaries = rows.Where(r => r.NativeType is not null and not 13).GroupBy(r => r.Model).Select(g => new
            {
                model = g.Key, references = g.Count(), panelQueuesEqual = g.Count(r => r.QueueEqual == true),
                exportableDistances = g.Count(r => r.CanExportDistances),
                gatesEqualAmongMatchingQueues = g.Count(r => r.SkipGatesEqual == true),
                replayFrames = g.Sum(r => r.ReplayFrames), replayStateMismatches = g.Sum(r => r.ReplayMismatches),
                maxAbsTailDeltaMsAt1000 = g.Where(r => r.TailDeltaMs is not null).Select(r => Math.Abs(r.TailDeltaMs!.Value)).DefaultIfEmpty().Max(),
                medianCompileMs = g.Select(r => r.CompileMs).Order().ElementAt(g.Count() / 2),
                maxSampleCompileMs = rows.Where(r => r.Model == g.Key).Max(r => r.CompileMs)
            }).ToArray();
            var report = new { prototype = "offline-arc-interval-v2", tests, contours, fans, summaries, cases = rows };
            string json = JsonSerializer.Serialize(report, JsonOptions);
            File.WriteAllText(Path.Combine(output, "comparison.json"), json, Encoding.UTF8);
            string template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "viewer.template.html"));
            const string placeholder = "__REPORT_DATA__";
            if (template.Split(placeholder).Length != 2)
                throw new InvalidOperationException("The report template must contain exactly one data placeholder.");
            string reportPath = Path.Combine(output, "report.html");
            File.WriteAllText(reportPath, template.Replace(placeholder, JsonSerializer.Serialize(report)), Encoding.UTF8);
            // Repair the old copied viewer.html in bin as well. It is an entry
            // page now, never a raw template that attempts to JSON.parse a token.
            string href = System.Net.WebUtility.HtmlEncode(Path.GetRelativePath(AppContext.BaseDirectory, reportPath).Replace('\\', '/'));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "viewer.html"),
                $"<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0; url={href}\"><title>打开判定模型报告</title><p><a id=\"reportLink\" href=\"{href}\">打开完整判定模型报告</a></p></html>", Encoding.UTF8);
            var summary = new StringBuilder("Sinmai-Alpha 判定数学离线原型 / 2026-11-02\r\n\r\n");
            summary.AppendLine("范围：本程序为离线报告；运行时接入独立放在 src/Notes/ExperimentalSlideJudge，默认关闭。统一表基线列属于开关内的较早重构，不是关闭时的原判定。");
            summary.AppendLine("直线/圆弧解析求交；变半径和不透明曲线自适应细分；真实路程区间、重复过区、ABC/ABCDE 配置、实验性跳区门控。");
            summary.AppendLine("候选区域包含圆、凹多边形轮廓和方向相关的任选容错，并非提取出的实体传感器边界。");
            summary.AppendLine("diameter-calibrated 仅用原版直穿中心滑条的径向距离校准候选圆；不会按输入形状套原版表。");
            summary.AppendLine("contours-and-choices 用少数基础直线/外圈弧的局部边界标定统一轮廓，再按径向/切向穿越补任选组。70 个参考中包含校准数据，不是独立盲测。");
            summary.AppendLine("新轮廓在 70/70 非 Wi-Fi 参考上匹配区域、选择优先级和跳区门控；2240 个旋转镜像、782592 帧针对性输入回放通过。");
            summary.AppendLine("Wi-Fi 三路状态机、末两区互通、下一帧完成判定和 LateGood 条件通过 131072 帧独立回放；没有验证完整计分时钟和实机表现。");
            summary.AppendLine("普通 ABC Wi-Fi 的两侧末端仍未加入原版 D5/D6 备选，因此生成几何不能宣称三路全等。D/E 扩展配置保持显式启用。");
            summary.AppendLine("外圈圆弧尾等待的几何比例已与参考吻合；复合曲线仍有约 22.38 ms/1000 ms 最大尾等待差，全表距离尚未完全一致。");
            summary.AppendLine("零长度/首尾未覆盖显式报告；不会虚构 C 区或修改末尾距离以让结果通过。");
            summary.AppendLine("边界误差按各自总距离归一化后换算成原型路径距离；尾等待差按滑行 1000 ms 比较，不代表完整判定窗口。");
            summary.AppendLine("性能为本机离线预生成用时，不能用来推断 Unity 游戏帧率。\r\n");
            summary.AppendLine(JsonSerializer.Serialize(tests, JsonOptions));
            summary.AppendLine(JsonSerializer.Serialize(contours, JsonOptions));
            summary.AppendLine(JsonSerializer.Serialize(fans, JsonOptions));
            summary.AppendLine(JsonSerializer.Serialize(summaries, JsonOptions));
            summary.AppendLine("\r\n浏览 report.html 查看路径、区域、逐项判定表；comparison.json 保存完整原始区间与差异。");
            File.WriteAllText(Path.Combine(output, "结果说明.txt"), summary.ToString(), Encoding.UTF8);
            Console.WriteLine(JsonSerializer.Serialize(summaries));
            Console.WriteLine("Report: " + Path.Combine(output, "report.html"));
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL " + e); return 1; }
    }

    sealed record Area(int[] Panels, double Push, double Release)
    {
        public string Label => string.Join("/", Panels.Order().Select(RegionModels.Name));
    }
    sealed record Comparison(string Code, string Model, string Profile, int? NativeType,
        string Queue, string? NativeQueue, string? CurrentQueue, bool? QueueEqual,
        bool CanExportDistances, double GeometricLength, double? NativeTableLength,
        double? NormalizedBoundaryError, double? TailDeltaMs, double? CurrentTailDeltaMs,
        bool? SkipGatesEqual, int ReplayFrames, int ReplayMismatches, double CompileMs,
        int ApproximateSegments, int Pieces, double EstimatedLengthDefect,
        IReadOnlyList<string> Diagnostics, IReadOnlyList<SensorVisit> Visits,
        IReadOnlyList<JudgeStep> Steps, IReadOnlyList<ChoiceWindow>? Choices, IReadOnlyList<Area>? Native,
        object[] Regions, double[][] Path);

    static Comparison Compare(string code, ParametricSlidePath source, SlideJudgeProfile profile, string model, JsonElement? reference)
    {
        var regions = RegionModels.Create(model, profile);
        var timer = Stopwatch.StartNew();
        var geometry = new ArcLengthPath(source);
        var table = IntervalJudge.Build(geometry, regions, model == RegionModels.Contours);
        timer.Stop();
        List<Area>? native = null;
        string? nativeQueue = null, currentQueue = null;
        double? nativeLength = null, normalizedBoundaryError = null, tailDelta = null, currentTailDelta = null;
        bool? queueEqual = null, gatesEqual = null;
        int frames = 0, mismatches = 0;
        int? type = null;
        if (reference is { } row)
        {
            type = row.GetProperty("type").GetInt32();
            native = row.GetProperty("native").EnumerateArray().Select(a => new Area(
                a.GetProperty("panels").EnumerateArray().Select(p => p.GetString() is "C1" or "C2" ? 16 :
                    (int)Enum.Parse<InputManager.TouchPanelArea>(p.GetString()!)).ToArray(),
                a.GetProperty("push").GetDouble(), a.GetProperty("release").GetDouble())).ToList();
            nativeQueue = string.Join(" → ", native.Select(a => a.Label));
            nativeLength = native.Sum(a => a.Push + a.Release);
            queueEqual = nativeQueue == table.Queue;
            tailDelta = table.TailWait(1000) - 1000 * native[^1].Push / nativeLength;

            // This branch is a comparison only. The prototype compiler above
            // has no reference to native fixtures or production hit tables.
            var current = SlideTable.Build(SlideDefinition.Parse(code), OptionMirrorID.Normal).Frames[0].Lanes[0].HitAreas;
            currentQueue = string.Join(" → ", current.Select(a => string.Join("/", a.HitPoints
                .Select(p => (int)p is 16 or 17 ? 16 : (int)p).Order().Select(RegionModels.Name))));
            currentTailDelta = 1000 * current[^1].PushDistance / current.Sum(a => a.PushDistance + a.ReleaseDistance)
                - 1000 * native[^1].Push / nativeLength;
            if (queueEqual == true)
            {
                double distance = 0, maxError = 0;
                for (int i = 0; i < native.Count; i++)
                {
                    maxError = Math.Max(maxError, Math.Abs(distance / nativeLength.Value * geometry.Length - table.Steps[i].Enter));
                    distance += native[i].Push;
                    maxError = Math.Max(maxError, Math.Abs(distance / nativeLength.Value * geometry.Length - table.Steps[i].Exit));
                    distance += native[i].Release;
                }
                normalizedBoundaryError = maxError;
                if (type != 13)
                {
                    var areas = native.Select(a => new SlideManager.HitArea { PushDistance = a.Push, ReleaseDistance = a.Release,
                        HitPoints = a.Panels.Select(p => (InputManager.TouchPanelArea)p).ToList() }).ToList();
                    var nativeReplay = new NativeJudgeReplay((SlideType)type.Value, 0, row.GetProperty("end").GetInt32() - 1, areas);
                    var prototypeReplay = new PrototypeReplay(table.Steps);
                    gatesEqual = true;
                    var random = new Random(170);
                    for (int i = 0; i < native.Count; i++) foreach (bool entered in new[] { false, true })
                    {
                        nativeReplay.Reset(i, entered);
                        prototypeReplay.Reset(i, entered, native[i].Panels[0]);
                        gatesEqual &= nativeReplay.NextCheck() == prototypeReplay.NextCheck();
                        for (int f = 0; f < 128; f++)
                        {
                            ulong mask = (ulong)random.Next(1 << 17);
                            nativeReplay.Step(mask); prototypeReplay.Step(mask); frames++;
                            var n = nativeReplay.State; var p = prototypeReplay.State;
                            if (n.Index != p.Index || n.Entered != p.Entered ||
                                n.Index < areas.Count && n.Entered && (int)areas[n.Index].HitPoints[n.SubIndex] != p.Selected) mismatches++;
                        }
                    }
                }
            }
        }
        return new(code, model, profile.ToString(), type, table.Queue, nativeQueue, currentQueue, queueEqual,
            table.CanExportDistances, geometry.Length, nativeLength, normalizedBoundaryError, tailDelta, currentTailDelta,
            gatesEqual, frames, mismatches, timer.Elapsed.TotalMilliseconds, geometry.ApproximateSegments,
            geometry.Pieces.Count, geometry.EstimatedLengthDefect, table.Diagnostics, table.Visits, table.Steps, table.ChoiceWindows, native,
            regions.SelectMany(r => r.Discs.Select(d => (object)new { kind = "disc", panel = r.Panel, name = RegionModels.Name(r.Panel),
                x = d.Center.Real, y = d.Center.Imaginary, radius = d.Radius })
                .Concat((r.Polygons ?? []).Select(p => (object)new { kind = "polygon", panel = r.Panel, name = RegionModels.Name(r.Panel),
                    x = p.Points.Average(q => q.Real), y = p.Points.Average(q => q.Imaginary),
                    points = p.Points.Select(q => new[] { q.Real, q.Imaginary }).ToArray() }))).ToArray(),
            Enumerable.Range(0, 401).Select(i => geometry.AtDistance(geometry.Length * i / 400))
                .Select(p => new[] { p.Real, p.Imaginary }).ToArray());
    }
}
