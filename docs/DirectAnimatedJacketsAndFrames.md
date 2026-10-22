# Direct Animated Jackets and Frames

曲绘、收藏品 Frame、头像和姓名框（NamePlate）的直读功能已经集成到 `Sinmai-Alpha.dll`，无需额外 DLL。
在现有 `Sinmai-Alpha/config.json` 中加入或修改 `Artwork` 对象，保留其他配置项：

```json
"Artwork": {
  "Enabled": true,
  "EnableJackets": true,
  "EnableFrames": true,
  "EnableIcons": true,
  "EnableNamePlates": true,
  "EnableImages": true,
  "EnableGif": true,
  "EnableVideo": true,
  "Directory": "LocalAssets"
}
```

默认 `Enabled` 为 `false`，其他媒体和分类开关为 `true`。旧配置缺少该对象时保持关闭。
`Directory` 相对游戏运行目录，也可以使用绝对路径；Windows 路径可用 `/`。
修改配置或资源后重启游戏。独立版的 TOML 不会被读取；启用 Alpha 内置版本前移除独立版 DLL。

## 文件布局

```text
Package/
  LocalAssets/
    Jacket/UI_Jacket_006072.gif
    Frame/UI_Frame_000001.mp4
    Icon/UI_Icon_709514.gif
    NamePlate/UI_Plate_000001.gif
  StreamingAssets/A698/AssetBundleImages/
    jacket/ui_jacket_006072.gif
    frame/ui_frame_000002.gif
    icon/ui_icon_709514.gif
    nameplate/ui_plate_000001.gif
```

使用游戏对应分类的资源 ID，不一定等于歌曲 ID。还会识别 `Sinmai_Data/StreamingAssets`、
AMDaemon 的 option 挂载根目录，以及 DataManager 实际加载的 Axxx/Mxxx 目录。
支持 `.gif`、`.mp4`、`.webm`、`.mov`、`.m4v`、`.png`、`.jpg`、`.jpeg`，扩展名不区分大小写。
曲绘可简写为 `LocalAssets/006072.gif`；Frame/头像/姓名框的数字文件必须分别放在 `Frame/`、`Icon/`、`NamePlate/` 下。
头像支持 `Icon.xml` 的 `fileName` 直接指定 GIF，也支持同名 GIF 替换 PNG 请求；旧配置缺少 `EnableIcons` 时默认开启头像分类。
姓名框使用 `NamePlate/UI_Plate_<ID>` 命名，支持 GIF 或视频替换同名 PNG 请求；`EnableNamePlates` 独立控制，旧配置缺省为开启。
`FrameMask` 不属于此功能。

## 优先级与回退

- `LocalAssets`（或配置的目录）高于挂载资源；挂载按游戏实际加载顺序，后加载者优先。
- 同一资源优先 GIF > MP4 > WebM > MOV > M4V > PNG > JPG > JPEG。
- 独立的动态 `_S` 文件优先用于缩略图；没有时完整动图可覆盖静态缩略图。
- 没有独立静态缩略图时，回退到完整 PNG/JPG。
- 无替代资源或首次解码失败时，保留游戏/其他 Mod 返回的纹理；视频异步失败时保留原封面。

外置谱面 Mod 若请求绝对路径 `bg.png`、`cover.png` 或 `jacket.png`，可以在同目录放
`jacket.gif`、`cover.gif`、`bg.gif`，或 `jacket.mp4` / `cover.mp4` 等支持的视频格式。
`bg.mp4` 保留 BGA 含义，不自动作为曲绘。保留原静态封面，供外置谱面扫描器发现歌曲和播放失败回退。

## 播放与资源

GIF 忽略文件内有限循环次数，视频始终循环并静音。视频使用独立时钟，不跟随谱面速度。
同一路径的大图和缩略图共享纹理及进度；画面隐藏时暂停，约两秒后释放解码器，再显示时重新开始。
透明 GIF 创建完整矩形精灵，避免首帧透明区域裁掉后续画面。

GIF 支持透明、局部调色板、交错、帧恢复和逐帧时长；零时长使用 100 ms，最短 20 ms。
单文件上限为 64 MiB、4096 帧、每边 4096 像素、画布 4,194,304 像素。
视频按原封面比例显示，曲绘/头像/姓名框纹理最长边 512，Frame 最长边 1024；素材比例应与原图一致。
建议使用 H.264/YUV420P MP4；容器扩展名受支持不等于其中所有编码都可用。
需要透明区域时优先 GIF/PNG。视频兼容性由 [Unity 和系统解码器](https://docs.unity3d.com/2018.4/Documentation/Manual/VideoSources-FileCompatibility.html)决定。

## 验证

```powershell
./build.ps1 -GamePackage '<game>/Package'
dotnet run --project tests/Settings/Tests.csproj -c Release
dotnet run --project tests/Artwork/Tests.csproj -c Release
```

Artwork 检查使用 .NET 10 SDK，不依赖游戏 DLL 或图形设备；运行时 DLL 仍为 .NET Framework 4.7.2。
生成测试数据的可选脚本 `tests/Artwork/generate-fixtures.py` 需要 Pillow：

```powershell
python tests/Artwork/generate-fixtures.py tests/Artwork/Fixtures
```

测试覆盖 GIF 像素/时长的三轮连续播放、disposal 1/2/3、透明、局部调色板、交错和 LZW、损坏输入，
以及 PNG/缩略图回退、曲绘/Frame/头像/姓名框分类隔离、挂载顺序、格式开关和 BGA 隔离。设置检查覆盖旧配置和部分 JSON 对象。

游戏内验证仍需启用配置，分别检查选曲大图/缩略图、循环末尾、切换歌曲后的隐藏恢复，以及收藏品 Frame、头像和姓名框。
编译和纯逻辑测试不能验证目标机器的视频解码器或图形驱动。
