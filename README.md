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

### 难度和模式和等级外观

- 难度：
  
在对应 MA2 旁放同名 `.ExtraDifficulty.flag`，内容为难度文件夹名，或 `None` 恢复原版。
例如 `011451_03.ma2` 配 `011451_03.ExtraDifficulty.flag`。
若前面无前缀，比如`ExtraDifficulty.flag`则只作用于 MASTER 难度。
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
  
可将原本的数字等级显示替换为自定义文字。在谱面 .ExtraDifficulty.flag 中加入：

```
Inscribed
CustomLevelText=true
LevelText=∞
AllowTapInHold=true
```

### 允许 Hold 夹 Tap

在逐谱 `.ExtraDifficulty.flag` 中增加独立一行 `AllowTapInHold=true`。
只有这一行时保留原版难度，只允许 Hold 期间判定 Tap。
可与难度目录名共存，例如：
```
Inscribed
AllowTapInHold=true
```

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
`EFFECTS` 列出当前演出特效名称、数值及命令参数，NZONE 生效/预警区域数量、COLORV/SIZEV/ALPHAV 状态、JLINE 及最近媒体指令。
零值特效保留最近两秒的事件信息；不是完整谱面副本。视觉流输出最多128项，超过时会标明截断。

分段为包含内部调用的 CPU 墙钟耗时，不能相加或当成 GPU 耗时。帧间隔还包含渲染、限帧、输入和等待；报告提供线索，不自动断言某特效就是根因。
分位数使用1毫秒桶估算，1001代表超过1000毫秒。后台写入队列上限256条，满时丢弃并记录数量，单次文件上限32 MiB；旧报告需要手动清理。
开启诊断会有额外开销，最终帧率需关闭诊断后复测；未完成玩家机台整曲验证。
