param([Parameter(Mandatory=$true)][string]$GamePackage)
$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$taskGame=[IO.Path]::GetFullPath($GamePackage)
$taskRefs=Join-Path $taskRoot 'Libs'
New-Item -ItemType Directory -Path $taskRefs -Force | Out-Null
[xml]$taskProject=Get-Content -LiteralPath (Join-Path $taskRoot 'Sinmai-Alpha.csproj') -Raw
foreach($taskRef in $taskProject.Project.ItemGroup.Reference){
 if(!$taskRef.HintPath){continue}
 $taskName=[IO.Path]::GetFileName([string]$taskRef.HintPath)
 $taskCandidates=@((Join-Path $taskGame ('Sinmai_Data/Managed/'+$taskName)),(Join-Path $taskGame ('MelonLoader/net35/'+$taskName)))
 $taskSource=$taskCandidates|Where-Object {Test-Path -LiteralPath $_}|Select-Object -First 1
 if(!$taskSource){throw ('Missing game reference '+$taskName)}
 Copy-Item -LiteralPath $taskSource -Destination (Join-Path $taskRefs $taskName) -Force
}
dotnet build (Join-Path $taskRoot 'Sinmai-Alpha.csproj') -c Release
if($LASTEXITCODE -ne 0){throw 'Build failed'}
$taskOut=Join-Path $taskRoot 'dist/Package'
New-Item -ItemType Directory -Path (Join-Path $taskOut 'Mods'),(Join-Path $taskOut 'Sinmai-Alpha') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'bin/Release/net472/Sinmai-Alpha.dll') -Destination (Join-Path $taskOut 'Mods/Sinmai-Alpha.dll') -Force
Copy-Item -Path (Join-Path $taskRoot 'assets/*') -Destination (Join-Path $taskOut 'Sinmai-Alpha') -Recurse -Force
# Keep release packages free of documentation, including leftovers from older builds.
Get-ChildItem -LiteralPath $taskOut -Recurse -File | Where-Object { $_.Name -match '^(README|LICENSE|LICENCE|LISENCE|NOTICE)(\..*)?$' } | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
# These Mono runtime dependencies are absent from the stock game's Managed
# directory. MelonLoader preloads UserLibs before initializing mods.
$taskUserLibs=Join-Path $taskOut 'UserLibs'
New-Item -ItemType Directory -Path $taskUserLibs -Force | Out-Null
foreach($taskName in @('System.Numerics.dll','System.Runtime.Serialization.dll','System.ServiceModel.Internals.dll')){
 Copy-Item -LiteralPath (Join-Path $taskRoot ('RuntimeDependencies/'+$taskName)) -Destination (Join-Path $taskUserLibs $taskName) -Force
}
Write-Output ('Ready: '+$taskOut)
