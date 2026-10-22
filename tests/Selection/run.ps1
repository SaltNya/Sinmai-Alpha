param(
    [Parameter(Mandatory=$true)][string]$GamePackage
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
dotnet build (Join-Path $PSScriptRoot 'Selection.Tests.csproj') -c Release --nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
$output = Join-Path $PSScriptRoot 'bin/Release/net472'
foreach ($name in @('MonoMod.RuntimeDetour.dll','MonoMod.Utils.dll')) {
    Copy-Item -LiteralPath (Join-Path $GamePackage "MelonLoader/net35/$name") -Destination $output -Force
}
Add-Type -LiteralPath (Join-Path $root 'Libs/Mono.Cecil.dll')
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($output)
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
# Unity/Mono accepts the game's PE metadata; desktop CLR needs normalized PE headers.
# Rewrite only disposable test copies. Method IL, original references and the game stay intact.
foreach ($name in @('Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll')) {
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $root "Libs/$name"), $reader)
    try {
        $nativeMethods = @($assembly.MainModule.GetTypes() | ForEach-Object { $_.Methods } | Where-Object IsNative)
        if ($nativeMethods.Count) { throw "Cannot normalize a genuinely mixed-mode test assembly: $name" }
        $assembly.MainModule.Attributes = $assembly.MainModule.Attributes -bor [Mono.Cecil.ModuleAttributes]::ILOnly
        $assembly.Write((Join-Path $output $name))
    } finally { $assembly.Dispose() }
}
& (Join-Path $output 'Selection.Tests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Selection regression failed' }
