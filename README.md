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

### 难度外观

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
https://github.com/Jian04/MajdataViewAlpha  Alpha制谱器

https://github.com/MuNET-OSS/MuConvert  转谱器

https://github.com/MuNET-OSS/AquaMai  Aquamai