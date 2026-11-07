在仓库根目录执行：

  dotnet build tests/ExperimentalSlideJudge/Math/Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false
  dotnet tests/ExperimentalSlideJudge/Math/bin/Release/net10.0/ExperimentalSlideJudge.Math.Tests.dll

  dotnet build tests/ExperimentalSlideJudge/Runtime/Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false
  tests\ExperimentalSlideJudge\Runtime\bin\Release\net472\Tests.exe off
  tests\ExperimentalSlideJudge\Runtime\bin\Release\net472\Tests.exe on Libs\Assembly-CSharp.dll

Math 使用实际新数学源文件，与原型逐项比较。原型作为只读对照，不随本次
接入修改，原有解析器通过 OfficialSlideParity 的轻量 Unity 数学替身复用。
Runtime 使用实际配置、缓存、输入状态和 Harmony 接入源文件；游戏对象和
几何编译结果是最小测试替身，验证启动时安装和真实 Harmony 字段/参数注入。
数值部分由 Math 单独验证；两者都不声称完成 Unity 实机计分测试。
Runtime 还执行从已恢复源文件复制的旧取表 transpiler 和箭头计数前置方法，
与新增接入一起安装，验证开启后旧重定向不会重复注入、关闭后仍走旧方法。

关闭和开启在不同进程运行，以验证启动时固定配置。临时配置位于随机测试
目录，测试后清理，不读写玩家真实 config.json，不启动/安装/修改游戏。
