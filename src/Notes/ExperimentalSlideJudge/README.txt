实验性滑条判定开关

配置
  在游戏目录 Sinmai-Alpha/config.json 中添加或修改：
    "ExperimentalSlideJudgment": false
  false：默认值；完全不安装本目录补丁，运行改动前的原判定实现。
  true：启动时启用实验判定。修改后需要重启游戏。
  旧配置没有这个字段时仍关闭；ChartFeatures=false 时不会启动此模块。
  仓库默认配置为 assets/config.json，默认已加入 false。

隔离范围
  本目录包含此前统一表重构和最新数学原型的全部新增逻辑。
  原来的 10 个滑条源文件已恢复到 e9fdda7 提交中的内容，包括：
    CustomNoteTypes.01.Registration.cs / CustomNoteTypes.21.SlidePaths.cs
    Libs/CustomSlideNoteData.cs / Libs/SlideDataBuilder.cs / Libs/SlideCodeParser.cs
    Libs/ParametricSlidePath.cs / Libs/NativeTouchSlideGeometry.cs / Libs/NativeDFanGeometry.cs
    NativeDFanNotes.cs / SlideAppearance.cs
  原文件中的旧字段、注册、取表、渲染读取全部保留。独立的 OnBeforePatch
  仅在开关为 true 时安装接入；false 不调用本目录的表生成或手动输入算法。
  Hold 夹 Tap 默认关闭是单独要求的设置修复，保留在原来的两个文件中。

结构
  Unified/：此前统一定义、ABC/ABCDE 策略、34×34 过区表、距离修正、镜像、
  B/C 起点解析和恒半径极坐标圆弧尾型识别、原版 V 表兼容及连段实际 owner。
  UnifiedSlideNoteData.TryParseInto 把完整新表写入原类既有字段；不替换游戏
  注册的 NoteData 类型。旧 FanVisualOffsets、TouchGeometry 等接口继续可用。
  UnifiedSlideHooks：启用时让旧 transpiler 透传，使用新 transpiler 捕获真正
  的连段子 NoteData；同时让连段箭头数量由完整链计算。关闭时旧方法照常运行。
  根目录的 ArcLengthPath/IntervalJudge 等：最新的轮廓/弧长数学原型。

启用范围
  所有自定义滑条先使用此前的统一表实现；单段 SC/SSS、自定义 D/E、
  TouchSlide 在支持范围内进一步使用 contours-and-choices 原型。
  普通自定义路径按 ABC 处理；显式 Touch/D/E 配置才加入 D/E。
  生成区域距离、候选顺序、短尾/转角跳区门控，以及重复区的释放后再进入。
  星星外观、轨迹渲染、声音、计分公式、延迟设置、自动播放及地雷反转仍由
  当前实现负责。新区域距离会影响尾部等待和手打完成时机。

暂不使用最新轮廓算法的范围
  原生 NoteData 始终走游戏原表。Wi-Fi、连接星星整条链使用 Unified 表；
  等效原版 V 形使用 Unified 中经过验证的原版 V 兼容表。
  连段在 ParseSlideCode 之后才分配 child/ConnectSlide，因此取表时再次检查，
  防止连接星星里混入单段轮廓算法。
  零长度、端点未被模型覆盖、超过计算预算或无效距离会保留 Unified 表并输出
  [Experimental Slide Judge] Fallback；按原因合并日志，避免重复装饰刷屏。
  这些范围仍待完整时序/实机验证，不能宣称实验模型已完全等同官方判定。

运行开销和生命周期
  数学求交在读谱时完成；每个音符只保留 8 个起点坐标系的判定表。
  不保留临时曲线细分、原始交点或报告数据。
  输入阶段只读取该路径的传感器，使用预生成的顺序/门控，不重新求几何。
  缓存使用弱引用键，重新读谱/对象复用时清理，退出游玩时整体清理。
  配置只读一次，不允许歌曲进行中切换规则。

验证
  tests/ExperimentalSlideJudge/Math：实际新数学代码与未改动的离线原型对照，
  包含镜像、八个起点坐标系、手动推进、重复区、退化回退和输入分配检查。
  tests/OfficialSlideParity：验证迁移后的 Unified 表、原版 V 兼容和既有快照。
  tests/ExperimentalSlideJudge/Runtime：使用游戏 API 替身执行实际 Harmony
  补丁和原取表 transpiler，检查开/关、参数注入、对象复用、配置编码、缓存
  回收、连段实际 owner 和箭头数量。关闭时两代新算法都不会运行。
  另读取本地 Assembly-CSharp.dll 核对方法和私有字段契约。
  上述是离线验证，不是 Unity 实机或玩家手打测试。
