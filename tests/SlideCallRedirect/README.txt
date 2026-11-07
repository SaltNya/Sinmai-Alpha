Run from the repository root (Windows / .NET Framework 4.7.2):
  dotnet build tests/SlideCallRedirect/Tests.csproj -c Release
  tests/SlideCallRedirect/bin/Release/net472/Tests.exe Libs/Assembly-CSharp.dll

The game DLL is read as Cecil IL metadata only. All executed Harmony patches
target local fixtures, including foreach/finally, mixed native/custom chains,
head/child notes with deliberately identical type/start/end fields, arrow counts,
star lengths and fan calls that supply a constant slide type.

The supplied game's six target methods are inspected to ensure every path,
hit-area and length call is redirected and the NoteData field receiver is
captured. Other compatible Assembly-CSharp.dll paths may be passed as additional
arguments for the same read-only inspection. No game files are modified.
