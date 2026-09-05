# Changelog

## 1.2.0-pre.4

Two things: the GPU side of the frame, now that `gpu per stage` can see it, and the question a
player with forty mods installed actually asks. The shadow passes were 86 % of a GPU-bound frame
in the first report that could measure them, and four of the five changes below come out of that
one number. The fifth is a second HUD that says which mod is which.

### Added

- **A mod profiler with its own HUD (Shift+F7).** Every attribution this mod had so far names a part
  of the *engine* — a stage, a renderer, a tick listener, a task code — and none of them answers
  the question somebody with forty mods installed actually asks, which is *which of mine is it*.
  The names in those tables are types, and a type names its assembly, and the mod loader knows
  which mod each assembly belongs to: that map is the whole mechanism. The renderer and tick
  profilers' decorators now resolve their owning mod once when they are wrapped and add their
  ticks to that mod's bucket as well, so the entire per-mod attribution costs one field add per
  measured call — everything else was already being paid. On top of the milliseconds it collects
  what each mod *does*, because a mod can read 0,00 ms and still be the reason a frame looks the
  way it does: Harmony patches (how many methods, how many of them somebody else's code, how
  many transpilers), registered block/item/entity/behaviour classes, and — from a prefix/postfix
  pair around the loader's own `TryRunModPhase` — what each mod spent in its load phases on the
  client and on the integrated server. `.komet mods` prints the same thing as text, `.komet
  report` carries it, `ProfileMods` and `ModHudVisible` configure it.

  The HUD is a second overlay in the opposite screen corner, Shift+F7 cycling off → compact →
  full exactly like F7 does — the shifted variant of the key this mod already owns rather than a
  key of its own, because the ones that look free are not (F6 was tried and is a minimap macro).
  The engine matches modifiers exactly and runs that pass before the modifier-ignoring fallback,
  so the two cannot trigger each other; `.komet mods hud` does the same for anyone who rebinds.
  It inherits the performance HUD's machinery unchanged — off-thread cairo raster, adaptive
  rebuild interval, the state machine that keeps a view change from flashing the previous view.
  What it *cannot* see is printed on it rather than left out: a Harmony patch runs inside the
  method it patches, so its time is booked to the engine (which is why the patch count sits next
  to the milliseconds); block entity ticks, mod worker threads and GUI dialogs are not
  attributed either. With the renderer profiler off — the default — only the Before stage is
  wrapped at all, and the HUD says so in both views instead of quietly reporting a fraction of
  the truth.
- **A warning at world join when Optimum or OptiTime is present.** Both replace the same
  engine code Komet does, each unaware of the other, and a bisect used to be the only way to
  find out. Now a dialog ("Hey - this is incompatible: Optimum v0.3.14 detected") opens a
  moment after the world is finalised, the same line goes to the chat, a warning goes to the
  log, and the report names the client in its header. Optimum is recognised by the marker
  type its fork compiles into VintagestoryLib, with the game version string as fallback;
  OptiTime by its mod id. Komet stays enabled — which side should win is the player's call.

### Changed

- **The far shadow cascade gets a coverage margin, and the throttle finally saves something
  while you are moving.** The far map is kept for two to four frames and reprojected exactly,
  which sounds like a three-quarter saving and was not one: a reprojection keeps a retained map
  correctly *positioned* but cannot extend what it *covers*, so the throttle had to redraw as
  soon as the camera moved 0,15 blocks — at 85 fps that is every frame while walking and every
  frame while flying. The whole mechanism only paid off standing still, which is exactly when
  nobody needs it.

  The far box is now drawn `ShadowFarBoxMargin` blocks wider than the fade needs (16 by
  default), and the throttle's movement limit rises to 0.9 × that in step. The argument is
  containment, not tuning: the box is a sphere around the camera, so a sphere of radius r+m
  drawn at one position contains the sphere of radius r around every position within m of it —
  verify pins that against the shader's own fade and UV-edge constants, with a negative control
  that the un-margined box really does escape. The far cascade then updates at
  `ShadowFarMaxSkip` whatever the player is doing, instead of every frame. The price is texel
  density: 16 blocks on a ~255 block cascade is about 6 % coarser far shadows, and the HUD's
  `shadow map` row prints the resulting texels per block next to the `shadow cadence` row's new
  `redraw after N blocks`. The margin follows the symmetric box — safemode and `.komet toggle
  shadowbox` put vanilla's cone back and the movement limit drops with it, or the missing
  coverage would be the hard cut-off line the limit exists to prevent. `.komet toggle
  shadowmargin`, a stress phase of its own, and the report's new `far cadence:` line (frames
  drawn, frames saved, the limit and the margin).
- **The near shadow cascade renders into a map of its own size, 4096 px by default.** The
  engine allocates both cascades from one expression, so the near map was as large as the far
  one — 7168 px with the extra quality step — for a cascade that covers vanilla's 39-block
  wedge, about 60 x 34 blocks: well over a hundred texels per block on an axis, against the far
  map's fifteen. The GPU stage timer priced it: `near 5,8 ms` of an 8,9 ms GPU frame, for a few
  dozen chunks, because a depth pass costs texels times depth complexity and not geometry. The
  near depth texture is now re-specified at `ShadowNearMapSize` right after the engine builds
  its framebuffers; the cost falls with the square of the size (4096 is a third of 7168). The
  shader's PCF spacing still comes from the far map, so near shadow edges come out a touch
  crisper, about as vanilla draws them at quality 2. `.komet shadownear 3072` (or `off`)
  changes it live and rebuilds the framebuffers; the report's `near cascade:` line and the
  HUD's `near map` row show the map and its texels per block. Texel snapping quantises each
  cascade to its own grid now that the two differ.
- **The solid passes draw into the shadow maps with a depth-only shader.** The engine's
  chunkshadowmap.fsh samples the terrain texture and discards below alpha 0.02 for every
  fragment of every pass, the solid cubes included. A shader with `discard` cannot write depth
  before it has run, so fragments behind an already drawn surface are still shaded, and every
  surviving one pays a texture fetch. Komet builds a program from the engine's live vertex
  shader (source, prefix defines, includes, attribute bindings copied, so SSBO mode, wind and
  a shader mod's replacement are reproduced) and an empty fragment shader, and binds it for
  the Opaque and TopSoil pools of the shadow passes; the foliage passes keep the engine's
  program. Rebuilt on every shader reload; a compile failure keeps the engine's and the report
  says so. `ShadowDepthOnlySolidPasses`, `.komet toggle shadowdepth`, a stress phase.
- **Back-face culling for the solid passes in the shadow maps.** Vanilla draws the whole
  shadow pass without culling. The Opaque and TopSoil pools are drawn WITH culling in the
  camera pass, so their winding is consistent and their volumes closed — and a closed
  volume's back faces lie behind its front faces along the light ray too. The depth map is
  the same; the GPU drops half of every solid face before a fragment exists instead of
  rasterising, testing and (in whichever order the pools come) writing and overwriting it.
  The foliage passes keep vanilla's no-cull: leaves and grass cast from either side.
  `ShadowCullBackfaces`, `.komet toggle shadowcull`, a stress phase, and the report's
  `solid backfaces culled` note.
- **`ShadowSkipRedundantLod` is on by default, and decided per grid cell.** It used to test
  whether the whole pool lay inside the LOD bias, which in a streamed-in world it never did
  (every report read `lod3 in`), so the option saved nothing. A cell's box bounds its parts,
  so a cell nearer than the bias drops its LOD 3 bucket with the same exactness; the stand-in
  only ever goes where its detailed twin is already in the map.
- **A new creature type's animation frames are generated on a worker before its first entity
  enters the world.** The engine generates them lazily on the main thread the first time each
  animation starts (measured against the game's shape files: chicken-rooster 11,9 ms for 13
  animations, "attack" 4,7 ms; the report's "anim most expensive 53,1 ms (pig)" is that cost
  on a bigger shape), always in the frame a new kind of animal first moves into view. The
  entity load hold is the window: the first held entity of a shape starts a worker that runs
  the engine's own cache-miss sequence (InitForAnimations, GenerateAllFrames for every
  animation), the entity and every later one of that shape stay held until it is done, and the
  main thread then finds the frames ready. A shape already in use by a loaded entity is never
  touched off-thread. `EntityAnimationPrewarm`, `.komet toggle animwarm`, HUD row `anim
  prewarm`, report line `anim prewarm:`.
- **The GPU stage line names the far cascade's cost per drawn frame** — `far 1,9 = 5,7 when
  drawn` — because the throttle's skipped frames used to dilute it to a third — and the frame
  as the stamps see it (`frame by stamps 9,8 ms`), so the stage figures can be read against
  their own frame when the elapsed query sampled other ones.
- Config layout 15: eight new settings since pre.3, five of them shadow-related. An existing
  `komet.json` is backed up next to itself and regenerated from the current defaults.

### Fixed

- **Crash on the first frame with the Optimum client build** — `MissingFieldException:
  ClientMain.MainThreadTasksLock` in the connecting screen, reported with v1.22.7 + Optimum
  v0.3.14. Optimum declares the lock of the main-thread task queue as a `System.Threading.Lock`
  where vanilla has a plain `object`, and a field reference compiled into the mod carries the
  field's type, so the vanilla binding no longer resolved on the fork. The task drain now reads
  the field by name and takes the lock the way its type demands: `Monitor` on the object,
  `Enter` on the `Lock`. The two are not interchangeable — a `lock` statement on a `Lock`
  instance would silently be a *different* lock than the one the network thread holds while it
  enqueues. A lock of any third kind leaves the drain vanilla's, with the usual "could not
  enable" line. Verify covers both forms and drains through a real `ClientMain` with the
  engine's own field.
- **A German client's log filled with "Translation string format exception"** — an error and
  a warning per HUD refresh for every label with a placeholder ("warteschlange {0}", "meist
  {0} {1} ms", ...), thousands per session. `Loc` asked the engine for the bare entry through
  `Lang.GetIfExists`, which formats even when given no arguments and throws on a placeholder;
  the engine catches that and logs it, every time. The entry is now read unformatted, and
  formatting happens only in the overload that has the arguments. Verify pins the contract
  with an injected table.

## 1.2.0-pre.3

Everything here answers a report from someone running pre.2, or follows from one.

### Fixed

- **Joining a world could crash the client** with a `NullReferenceException` in
  `WeatherSystemClient.OnRenderFrame`. pre.2 introduced a per-frame budget on the main-thread
  task drain, and a world join queues hundreds of those tasks — including the one that
  finalises the level. Spreading that backlog over frames let a renderer that was already
  registered draw before the packet initialising it had run. The budget now starts only once
  the world is finalised: everything a join queues runs in the frame it arrives, exactly like
  vanilla. Joining is lifecycle, not load, and it is not the place to save milliseconds.
- **Two budgets could stop working instead of falling back.** The entity tesselation and
  entity loading budgets reopen their window on the frame boundary and nowhere else. If that
  signal ever stopped arriving while the patch stayed applied, the tesselation budget skipped
  *every* further entity shape — permanently, for every animated creature in the world — and
  held entities were never loaded. Both now notice the missing signal, run vanilla instead,
  and count it on the HUD; and neither arms itself at all if the measurement it depends on
  failed to apply. A budget that loses its reset has to degrade to slower, never to never.
- **The patch guard was quiet about the collisions that matter most.** Where Komet replaces an
  engine method outright (its prefix returns false), another mod's transpiler never runs, a
  prefix ordered behind Komet's is never called, and a postfix sees the result of Komet's
  transcription rather than the original's. All three used to be reported as "info". They are
  now high/high/medium with the reason spelled out — this is the shape behind reports of the
  "entities are invisible since I installed this mod" kind, and it should not need a bisect.

### Added

- **German HUD and chat.** The F7 overlay and the `.komet` replies follow the game language;
  English and German ship in `assets/komet/lang`. Missing translations fall back to the English
  text in the source, never to a raw key.
- **Reproducible release builds with a published checksum.** The same commit produces the same
  bytes, so the SHA-256 on the download page can be recomputed rather than merely trusted. The
  number is also published on the GitHub release, written by the build job.

### Changed

- **Logs, reports and hitch lines are English.** They used to be German. These end up in bug
  reports and get read by people who do not run the client that produced them — the HUD and the
  chat replies are the surfaces that follow the player's language, and nothing else does.
- **The mod zip is stored, not compressed** (~180 KB → ~450 KB). Identical data does not give
  identical deflate streams across zlib implementations, so compressing would have made the
  checksum unreproducible on a different machine. The download is larger; the promise holds.
- The unused `MeasureRenderStages` setting is gone from the config class. It was never read.
  The config layout version stays at 11 on purpose: an existing `komet.json` keeps working,
  the leftover key is ignored, and nobody's settings get regenerated over a dead entry.

### Internals

- Every build runs on GitHub Actions: both mods built, the full patch and behaviour suite run
  against the real game assemblies, the packaged zips attached to the run, previews published
  under Releases. The SDK version is pinned in `global.json` — a different patch level produces
  a different assembly, which is measurable and would otherwise break reproducibility silently.
- Source folders sit next to the project file and each one is its own namespace
  (`Culling/` → `Komet.Culling`, `Measure/` → `Komet.Measure`). Not cosmetic: an editor rule
  that derives namespaces from paths had rewritten a generated file and silently disabled the
  engine-drift check.

---

Earlier releases are documented as paste-ready HTML for the ModDB page in
[`moddb/`](moddb/) — see `changelog-1.2.0-pre.2.html` and `changelog-1.1.0.html`.
