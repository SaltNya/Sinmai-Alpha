Unified slide tables

Run from the repository root:
  dotnet run --project tests/OfficialSlideParity/Tests.csproj -c Release
  dotnet run --project tests/SlideCallRedirect/Tests.csproj -c Release

Optional pipeline (ExperimentalSlideJudgment=true):
  ExperimentalSlideJudge/Unified contains this entire earlier refactor.
  SlideDefinition.Parse -> SlideTable.Build -> UnifiedSlideNoteData.TryParseInto
  publishes to the original CustomSlideNoteData fields without editing that class.
  The original source files are restored; the flag off uses their old pipeline.
  Only SlideDefinition understands legacy SC/TG1/DG1/DG2/DF1 spellings.
  One sensor-pair matrix is generated once. Custom lanes share the sampler,
  transition table and mirror/rotation conversion; fan rendering keeps its frame.
  NativeVSlide is a narrow exception for geometry equivalent to native v/V:
  ABC-only straight legs with a native center/key corner read the running game's
  hit-area table and keep its native SlideType, enabling SlideRoot.NextCheck's
  mandatory checkpoints. Touch/D/E paths and non-native turns stay geometric.
  Tables are per note, prepared at chart load, with no global chart cache.
  Source geometry is released after compilation except for touch SV motion.

CompatibilityBaseline.json was captured BEFORE the table refactor on 2026-11-02.
It contains SHA-256 digests for 532 inputs/mirrors. Each digest covers all eight
star frames, all lanes, arrow coordinates/orientation/distance, hit panels and
push/release distances, end type, fan endpoint/visual rotation and touch motion.
Do not regenerate this fixture merely to make a regression pass.
The native v/V fix intentionally changes 60 entries; the other 472 must remain
bit-for-bit equal. NativeVTests independently checks the changed cases against
the native fixture: 480 rotations/mirrors, 3840 star-frame tables, tail wait
windows and 339968 frames of headless manual input progression.

All geometry adapters and the table pipeline are linked from production sources.
PortableMath.cs and the Z-only quaternion shim replace Unity math for headless
testing. NativeJudgeReplay mirrors the relevant 1.70 NextCheck/CheckSlideTouch
branches, with deterministic sensor masks. These checks do not run Unity
rendering or capture real player input.
Native170.json is a comparison fixture. The test manager exposes only v/V data
to the production compatibility rule; every other native table lookup throws.

70/70 stock non-fan shapes have equal panel queues (C1/C2 normalized), including
the restored Finale return-v. Native-equivalent v/V now uses native distances,
C1/C2 representation, type and skip rules; other shapes still use geometric
distances. Original MA2 slides continue to use native game tables.
