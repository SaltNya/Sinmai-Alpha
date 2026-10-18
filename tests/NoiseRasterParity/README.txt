NoiseRasterParity (managed CPU regression, not a Unity/HDD run)

Requirements: Python 3 and .NET SDK 10. Run from this directory:
  python prepare.py
  dotnet run --project Tests.csproj
On Windows, run.ps1 performs both steps and stops if extraction fails.

prepare.py extracts the current production raster, style propagation, cache,
Prepare CPU gate, geometry and CanDraw expressions. It writes only ignored
obj/ files. OldOracle.json retains minimal verbatim pre-sync PC methods from
commit 26c07d5905c3d2ec137861da261580bd539300a2, with original source/body hashes.
The oracle is deliberately independent of the optimized implementation.

The fixture compares masks/styles exactly across the geometry matrix,
first-writer overlaps, warning raster, empty output, transition boundaries,
signed zero, non-finite cache keys, warm cache, A-B-A, dimensions and Clear.
It also executes the extracted Prepare cache condition against a fresh old
raster and checks camera/owner/geometry/matrix/viewport/color/span invalidation
and old/new no-copy guard equivalence. Projection and Unity value types are
explicit managed doubles. This does not test native Unity projection, shaders,
Mono/JIT speed, physical touch or a full HDD session. Existing NoisePerformance
Unity probes remain a separate future native/GPU check.

Result and exact source extraction hashes are in obj/result.json and
obj/source-extracts.json. A passing result is output parity/control-flow
evidence only; it is not a stable-60-FPS claim.
