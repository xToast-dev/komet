# Changelog

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
