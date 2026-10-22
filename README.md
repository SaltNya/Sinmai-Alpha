# Sinmai-Alpha
```
与AquaMai的兼容性仍需要测试，可能有奇怪的Bug
请关闭AquaMai过新过热中的CustomNoteTypes、ExtraDifficulty、ReviveFinaleVSlide、TapInHoldFix。
```
## 为Sinmai添加了地雷键、TouchStar、Hold/TouchHold头的Slide、相邻键直线的Slide比如1-2-3-4[4:1]、SSS/D 区滑条、SlideCode、SV/HS、弹跳出现note/修改note出生位置/note透明度、独立音符流、假键、噪域、字幕及多种屏幕特效

### 安装

将Release中的 `Mods`、`Sinmai-Alpha` 和 `UserLibs` 一起放入`/Package`。
DLL 位于`/Package/Mods/Sinmai-Alpha.dll`；贴图和特效包位于
`Package/Sinmai-Alpha/{ExtraDifficulty,CustomNoteTypes,Alpha}`。

### 谱面校验
有校验，只有检测到谱面中包含Sinmai-Alpha要素的时候才会启用模组特性，也就是游玩原版谱面不会有被此模组影响的可能。
A000 内的谱面始终绕过扩展玩法；其余 MA2 仅在包含自定义音符、扩展字段或有效特效命令时启用。
纯原版谱不加载自定义音符资源、不分配额外音效通道，也不执行 Alpha 的音符池扩容和判定修改。

### Direct Animated Jackets and Frames（动态曲绘与收藏背景框）

主 DLL 内置曲绘和收藏品 Frame 的 PNG/JPG、GIF 与视频直读，默认关闭。
在 `Sinmai-Alpha/config.json` 中把 `Artwork.Enabled` 设为 `true` 后重启游戏。
该功能独立于谱面玩法校验，启用后可替换任意曲绘/Frame，不改变游玩 BGA 或谱面行为。
GIF 和视频播完从头循环，视频静音；同一文件的大图与缩略图共享播放进度。
无需另装 `DirectAnimatedJacketsAndFrames.dll`；从独立版迁移时移除它，并在 Alpha 配置中重新设置开关。

例如在游戏目录放入 `LocalAssets/Jacket/UI_Jacket_006072.gif` 或
`LocalAssets/Frame/UI_Frame_000001.mp4`。也支持
`StreamingAssets/Axxx/AssetBundleImages/jacket/`、`frame/` 下的同名文件。
资源 ID 不一定等于歌曲 ID；完整曲绘可以直接用于 `_S` 缩略图。
支持 GIF、MP4、WebM、MOV、M4V；视频编码能否播放取决于游戏 Unity 和系统解码器。

完整配置、覆盖顺序和文件布局见 [动态曲绘说明](docs/DirectAnimatedJacketsAndFrames.md)。

### 难度和模式和等级外观

- 难度：
  
在 `Music.xml` 的 `MusicData/notesData` 中，找到目标谱面 `file/path` 所属的 `Notes`，加入以下节点：
```xml
<X-SinmaiAlpha>
  <Difficulty>Inscribed</Difficulty>
  <AllowTapInHold>true</AllowTapInHold>
  <CustomLevelText>true</CustomLevelText>
  <LevelText>∞</LevelText>
</X-SinmaiAlpha>
```
`Difficulty` 是难度目录名，`None` 恢复原版。每个难度分别保存，不能放在 `MusicData` 根节点。
新版 MCM 保存难度外观或 Hold 内 Tap 设置时，会迁移同曲已有谱面的旧标记；XML 保存成功后才删除对应 `.flag`，未知旧选项保存在 `LegacyOptions` 中。
存在 `X-SinmaiAlpha` 时以整个节点为准，即使它为空也不会回退到旧文件。没有节点时仍兼容同名 `011451_03.ExtraDifficulty.flag`；无前缀的 `ExtraDifficulty.flag` 仍只用于 MASTER。
仅支持旧 `.flag` 的旧版 mod 不会读取这些 XML 设置，使用新格式需同步更新 mod 和 MCM。
仅改变外观，不改游戏实际难度索引和成绩记录。

通过 `Sinmai-Alpha/ExtraDifficulty/<难度目录>/difficulty.json` 定义该难度：
```json
{
  "name": "INSCRIBED",
  "color": "#a879e8",
  "textureCode": "INS",
  "textureNumber": "08",
  "aliases": ["INS"]
}
```



- 模式：
  
Alpha 模式通过 MA2 文件头中的标签启用：

`// SINMAI_ALPHA_MODE	Alpha`

SINMAI_ALPHA_MODE 与 Alpha 之间必须使用 Tab 制表符。手动添加时可放在 VERSION 行之后。
支持此功能的 MuConvert-Alpha 会在转谱时自动写入标签。
模式外观以标签为准，不依靠检测扩展要素决定是否显示。带标签的谱面会在对应的选曲、开场及结算界面显示 Alpha 模式标识。
此功能仅替换模式贴图，实际谱面类型仍沿用原本的标准／DX 类型，判定规则与成绩记录方式保持原有类型。
贴图位于：

`Sinmai-Alpha/ExtraMode/Alpha/`

移除 MA2 中的模式标签可恢复原版模式标识。



- 自定义等级文字：
  
可将原本的数字等级显示替换为自定义文字。在该谱面的 `X-SinmaiAlpha` 中设置 `CustomLevelText` 为 `true`，`LevelText` 为所需文字（如 `∞` 或 `LV15+`）。关闭时将 `CustomLevelText` 设为 `false`。文字中的 `&`、`<` 需要按 XML 规则转义，MCM 保存时会自动处理。

### 允许 Hold 夹 Tap

在该谱面的 `X-SinmaiAlpha` 中设置 `<AllowTapInHold>true</AllowTapInHold>`。
`false` 显式关闭；删除此子节点则跟随默认行为，不会重新读取旧 `.flag` 中的值。仅填写此开关、不填写 `Difficulty` 时保留原版难度外观。

### 自定义版权文字

自制歌曲的 `Music.xml` 可以写：
```xml
<rightsInfoName>
  <id>Alpha</id>
  <str>冰冰冰</str>
</rightsInfoName>
```
`str` 使用游戏字体将自定义文字显示在版权栏即乐曲下面显示一行字。


### 鸣谢
- [MajdataViewAlpha](https://github.com/Jian04/MajdataViewAlpha)：Alpha 制谱器。
- [MuConvert](https://github.com/MuNET-OSS/MuConvert)：转谱器。
- [AquaMai](https://github.com/MuNET-OSS/AquaMai)：模组框架及相关实现。


### 性能问题报告（默认关闭）

`Sinmai-Alpha/config.json` 默认 `ShowSssJudgePath: false`、`GameplayDiagnostics: false`。
需要排查时将 `GameplayDiagnostics` 改为 `true`，重启游戏，复现卡顿后正常退出。
报告保存在 `Package/Sinmai-Alpha/Diagnostics/gameplay-日期时间.log`。
将本次日志连同歌曲名称、是否自动播放、是否录屏、卡顿附近的视频或描述发给维护者；不自动上传。
完成后将开关改回 `false` 并重启。旧配置中的显式值会保留，缺少字段时使用默认值。

报告包含硬件、显示设置、歌曲编号与难度、自动播放状态、谱面毫秒时间、内存、GC、异常及补丁归属。
每五秒记录累计帧间隔/更新耗时统计（平均、最大、p95/p99、超25/50/100毫秒次数）；结束时记录累计汇总。
超过25毫秒的卡顿每秒最多一条，累计计数不丢帧。分段包括音符更新/激活、屏幕特效、噪域更新/合成。
画面移动、旋转和缩放还会记录图层检查 `HasLayerCollision`、对象租约 `LeaseRenderers`、额外摄像机渲染 `RenderIsolationCamera`，帮助区分扫描和渲染等待。
`EFFECTS` 列出当前演出特效名称、数值及命令参数，NZONE 生效/预警区域数量、COLORV/SIZEV/ALPHAV 状态、JLINE 及最近媒体指令。
零值特效保留最近两秒的事件信息；不是完整谱面副本。视觉流输出最多128项，超过时会标明截断。

分段为包含内部调用的 CPU 墙钟耗时，不能相加或当成 GPU 耗时。帧间隔还包含渲染、限帧、输入和等待；报告提供线索，不自动断言某特效就是根因。

地雷普通箭头池和绝赞箭头池各默认预建至少 1024 个，也不按 `ExtraPoolCount` 倍增。进曲时在谱面管理器绑定完成后，仅针对超过 1024 箭头的地雷连接长链，按当前谱面各类型最长链的实际需求预建对应箭头池（普通和绝赞分别计算）。长链注册时若有其他轨道占用导致空闲不足，再补齐缺口并记录 `[Slide Pool]`；这种兜底扩容仍可能短暂卡顿。手动消除轨道只遍历实际分配的箭头，几何数量在单次判定中仅计算一次；判定区域和命中进度不受此边界保护影响。
分位数使用1毫秒桶估算，1001代表超过1000毫秒。后台写入队列上限256条，满时丢弃并记录数量，单次文件上限32 MiB；旧报告需要手动清理。
开启诊断会有额外开销，最终帧率需关闭诊断后复测；未完成玩家机台整曲验证。

诊断开启时，`WIFI` 按每条 WiFi 记录最终判定、判定偏差、三路判定区进度与完成时间；`NaN` 表示该路没有记录到全部完成。
`TINT BAKE` 记录首次生成颜色贴图的名字、颜色、尺寸和耗时，用于区分颜色生成与其他特效造成的卡顿。

### 噪域渲染优化

噪域遮罩、颜色和纹理上传按可见区域、演出时间、尺寸、颜色、几何版本、摄像机矩阵和视口精确失效；生长、收缩及画面变换仍更新。每个玩家的缓存独立，清理和尺寸变化会释放或重建缓存。
每个区域还会复用完全相同投影坐标对应的扫描行边界；每次调用仍执行完整投影，非有限坐标和退化区域沿用原计算。
二值遮罩及精确同色区域使用批量填充，多色区域保留原覆盖顺序和颜色传播。预警边缘按连续像素段扩展，不新增运行时 DLL 依赖。
没有可合成的有效噪域时跳过噪域所需的整帧复制。分辨率、颗粒、预警、发光合成和输入判定保持原有设置；CPU 遮罩复用时，GPU 动画仍逐帧运行。

离线回归：`tests/NoiseRasterParity/run.ps1` 检查真实生产方法与旧电脑版的像素结果、缓存失效和双玩家隔离；`dotnet run --project tests/NoiseWarningDilation/NoiseWarningDilation.csproj -c Release` 检查预警扩展。前者需要 Python 3，两者需要 .NET 10 SDK。
这些检查不替代 Unity/Mono、显卡或玩家机台的整曲帧率测试。
