# Selection-Only Regression

This change is limited to hiding disabled charts and fixing +/- navigation and
pooled button visibility. It follows the existing `ExtraDifficulty` setting.

- Uses the existing five native difficulty slots only.
- Normal songs show only enabled charts; +/- skips gaps and disappears at an endpoint.
- Each adjacent song card resolves its own available difficulty.
- Genre selection, native special songs and utage retain their existing behavior.
- No new difficulty registration, Emoji, network fallback, score format, or XML writes.

Supply local game dependencies in the ignored `Libs/` directory, then run:

```powershell
powershell -NoProfile -File tests/Selection/run.ps1 -GamePackage '<game directory>'
pwsh -NoProfile -File tests/Selection/run-game-mono.ps1 -GamePackage '<game directory>'
```

The first command builds the Mod and tests, normalizes only the disposable game
DLL copies in the test output for desktop CLR, and runs the tests. The second
executes the same tests in the original game's Mono runtime in a dedicated host
process. Neither command launches or modifies the real game installation.

Coverage includes all 32 native availability masks, resolution of absent current
charts, 432 complete stock selection workflows, and 1,280 sparse transitions on
the original game methods. Both players, tabs, directions, state changes and
sound lookup are checked. All 24 patch targets/IL are audited.

Native rendering/audio endpoints are stubbed in the workflow tests. Original-game
button animation, card layout and visual behavior still require in-game validation;
offline tests are not a substitute for that acceptance pass.
