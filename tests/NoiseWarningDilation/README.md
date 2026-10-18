噪域预警遮罩的离线对照测试，直接编译相对路径下的真实 helper，不需要游戏、Unity 或 ZXing。

需要 .NET 10 SDK。在仓库目录运行：

```powershell
dotnet run --project tests/NoiseWarningDilation/NoiseWarningDilation.csproj -c Release
```

默认将 JSON 结果写入测试项目的 `bin` 输出目录。也可以指定报告路径：

```powershell
dotnet run --project tests/NoiseWarningDilation/NoiseWarningDilation.csproj -c Release -- --output result.json
```

测试保留旧版两遍逐像素算法作为独立对照，比较最终遮罩、横向中间遮罩和数组尾部。覆盖任意非零字节、空尺寸、短边、边界连续区域、固定种子随机输入、尺寸交替和缓存清理。

报告中的短 CPU 计时仅作本机参考，不代表 Unity/Mono 游戏帧率，也不用于判定输出是否正确。
