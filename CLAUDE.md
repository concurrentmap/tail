# TAILED — working notes for Claude

Friendslop driving game: one Mark runs errands through simulated traffic; Tails follow
while blending in. Design: `docs/game-design.md`. Architecture + milestones: `docs/architecture.md`.

## Where things are
- Repo lives on NTFS at `/mnt/c/dev/vehicle_chase` (Unity needs it there). Always use that path.
- `core/Runtime/` — pure C# simulation core (no `UnityEngine`). Unity consumes it as local
  package `com.tailed.core`; `dotnet/Tailed.Core` compiles the same files for WSL testing.
- `unity/` — Unity 6000.5.2f1 URP project. Game code in `unity/Assets/Tailed/`.

## Commands
- `tools/unity.sh core-test` — dotnet tests for core (~6 s). Default loop for logic.
- `tools/unity.sh compile` / `tools/unity.sh test [EditMode|PlayMode]` — via editor bridge if the
  editor is open, else batch mode (~40 s cold). Logs: `out/logs/`.
- `tools/unity.sh open` then `tools/bridge.sh <cmd>` — ping, status, refresh, play, stop,
  run-tests, screenshot (PNG in `out/screens/`, view it with Read), console, execute, quit.
- `tools/unity.sh build [--dev]` — needs the editor closed (`tools/bridge.sh quit`).
- dotnet SDK is in `~/.dotnet` (unity.sh sets PATH).

## Looking at the game
- Scene `Assets/Tailed/Scenes/Town.unity` is generated: `tools/bridge.sh execute Tailed.EditorTools.TownSceneSetup.Create`
  (re-run after changing scene setup; don't hand-edit the scene).
- `tools/bridge.sh play`, then camera presets via `execute Tailed.Cameras.DevViews.<Overview|Downtown|Suburb|Industrial|Street|Junction>`,
  lane overlay via `execute Tailed.Map.LaneDebugView.Toggle`, new town via `execute Tailed.Map.TownBuilder.NextSeed`,
  then `tools/bridge.sh screenshot` and Read the PNG. Always look at the result after visual changes.
- Stop play mode (`tools/bridge.sh stop`) before `refresh` so the reload applies cleanly.
- Real-resolution checks (1920×1080 built player): `tools/unity.sh build --dev`, `tools/solo.sh start`, then
  `tools/solo.sh execute Tailed.Cameras.DevViews.MirrorRange` (test rig), `...GlanceRear|GlanceLeft|GlanceRight`,
  `...FocusOn`, `...RangeNextSolo`, `...MirrorReport` (px per plate character), `tools/solo.sh screenshot`.
- Multiplayer: `tools/mp-test.sh start 2 -tailed-quick -tailed-cheat-bots`, `status`, `shot c1`, `stop`.
- Keep bridge polling loops short; don't leave long watch loops running.

## Rules
- Keep `core/` free of UnityEngine references (asmdef enforces `noEngineReferences`);
  C# 9 only (Unity parity, enforced by the csproj).
- Put logic in `core/` with dotnet tests; Unity layer is adapters, views, physics, rendering.
- Randomness: `Tailed.Core.Util.Rng` only (deterministic across runtimes) — never System.Random
  or UnityEngine.Random in simulation code.
- Information hiding: never add a wire field that reveals which vehicle is a player.
- Plate legibility is computed from the driver's eye (direct or via mirror), never the chase cam.
- Voice is deferred — don't build it until M6.
- Art: friendslop — readability over detail. Flat vertex colours via `Tailed/Toon`, no textures except
  plates/signs; every vehicle model must be identifiable by silhouette. See docs/game-design.md §13.
- The town is fully derived from its seed (multiplayer sends only the seed); keep generation deterministic.
