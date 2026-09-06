# Changelog

## 1.2.0

`.komet` opens a window now. Everything it shows already existed — in the F7 overlay, in the
chat replies, in `.komet report` — and the work here was almost entirely about making sure it
stays that way: the window takes no measurement of its own, and the blocks it draws are the
same methods the overlay and the report call, split out rather than copied.

### Added

- **One dynamic worker pool instead of four thread sets that could not see each other.**
  Komet used to hold five cull helpers, four occlusion helpers, a dedicated window-prebuild
  thread and a dedicated unpack-prefetch thread, and hand the animation prewarm and the HUD
  raster to the shared .NET ThreadPool on top. Each set sized itself against the core count in
  ignorance of the others: eleven threads on a six-physical-core machine, next to the render
  thread, the engine's tesselation thread and the integrated server's worldgen threads. None
  of them could lend a thread to another, and the two that mattered most — the sweep on the
  frame's deadline and the occlusion walk that holds cores for milliseconds — collided often
  enough that a whole niceness mechanism exists to keep them apart.

  `JobScheduler` replaces all of it. Workers are not assigned to a workload; they take
  whichever queued job is worth most. Two shapes share the pool: **fork/join** batches where
  the caller blocks (the sweep at Critical, the occlusion walk at Background — slices handed
  out through one interlocked counter, the caller draining too, completion counted in *work*
  rather than in workers so a helper that never woke cannot stall anybody) and **fire-and-forget**
  jobs with a dedup key (the window prebuild at High, the neighbour unpack at Normal, the HUD
  raster at Background, the animation prewarm at Idle). Both batch workloads were already
  sliced to tens of microseconds, so a Critical job waits about what an OS wake costs anyway.

  Measured on this machine (`./build.sh bench`, five runs each): the sweep's per-frame cost is
  **0,70 ms median at four pool workers against 0,77 ms at the old five dedicated helpers** —
  the same work on fewer threads, which is the point.

  What the scheduler guarantees, and what the checks pin: every slice of every batch runs
  exactly once, under a three-second storm of Critical and Background batches from two threads,
  keyed submits from a third, and cancel/resize storms from a fourth. A key already queued or
  running is refused rather than doubled — that property *replaced* the prefetcher's
  hand-written "already seen" set. Cancellation drops queued fire-and-forget work whose world
  went away, but a **batch ticket is never cancelled**: its caller is blocked on it right now,
  and the first version that cancelled those produced batches with slices that had silently
  never run. Every Nth take starts at the bottom of the queues, which is what bounds the wait
  of a prewarm that nothing would otherwise ever let through. Counter-checked three ways —
  a batch ticket that honours cancellation, a dedup key that is never released, and a stale
  ticket that does not count its items out — each of which fails the suite.

  **The worker count adapts.** The ceiling is physical cores minus two (one for the render
  thread, one for the tesselator — the two this pool must never take a core from), capped at
  eight, floor one; hardware threads are deliberately not counted, because both batch workloads
  are memory-bound linear scans and an SMT sibling adds queueing, not throughput. From there it
  gives a worker back when a frame runs past 1,5× the rolling average with the pool busy, takes
  it again when the tesselation backlog says the pool's jobs are on the critical path of what
  the player is waiting for, and drops to one when nothing is queued. GC pauses are subtracted
  from the frame first, for the same reason the upload throttle subtracts them: a pause freezes
  every thread, so it is not evidence that the pool is competing with the render thread.
  Workers are never created or destroyed for this — the ones above the target park.
  `WorkerThreads` in komet.json is the ceiling; 0 derives it.

- **`CullingThreads` and `OcclusionCullingThreads` are gone from komet.json**, replaced by the
  single `WorkerThreads`. They described a thread topology that no longer exists — two
  independently sized sets — and leaving them in the file to control nothing is the failure
  mode this project spends most of its comments guarding against. The config layout goes to 25,
  so existing files are backed up and regenerated.

- **`.komet` → Threads/Jobs is a live worker monitor.** Workers busy/awake/idle and pool
  utilisation, queue depth, jobs/s, completed, cancelled and duplicates dropped, the caller's
  wait per batch, and the main-thread handoff queue — then one row per worker with its state
  (`CULLING`, `MESHING`, `LOADING`, `OCCLUDING`, `RASTERING`, `WARMUP`, `WAITING`, `IDLE`,
  `PARKED`), the chunk it is on and how long it has been on it, and a per-workload breakdown
  with queue depth, average and longest job. The report carries the same figures as two lines.
  It reads volatile fields without a lock — a row one job out of date is the right trade for a
  monitor that costs the pool nothing.

  There is deliberately no `GENERATING`, `TESSELLATING` or `UPLOADING` state. Chunk generation
  is the integrated server's worldgen threads, chunk loading is the network thread plus
  main-thread tasks, and tesselation, meshing and the GPU upload are engine threads no mod can
  schedule — the tesselator because `BlockEntity.OnTesselation` is a public extension point
  that every content mod implements against a single-thread contract, the upload because it
  needs the GL context. Inventing those states would be a lie in the one place people look to
  find out where the time went.


- **A far LOD: beyond the far distance a chunk is drawn as cells of two blocks, beyond twice
  that as cells of four.** Two reports from the same spot decided it: looking at the ground,
  189 fps with 125.000 triangles in the camera pass; looking at the horizon at view distance
  1536, 53 fps with 16,1 million, 12,7 million of them beyond 640 blocks where a block covers
  one to three pixels. Nothing on the CPU side was near a limit (the sweep 2,2 ms on five
  threads); the GPU pass probe put the camera opaque pass at 11,7 ms for 15 million fragments
  on a 3,7-megapixel frame - so not fill rate, and at 1,4 billion triangles per second not
  primitive rate either, but the front end: the chunk vertex shader exports thirteen vec4 per
  vertex and evaluates 3D value noise up to eight times, and every sub-pixel face pays that
  for two vertices. The only lever is fewer faces out there, and the merged far mesh of the
  previous build had already shown that merging cannot deliver them: rolling terrain has no
  coplanar runs (1,3 faces per rectangle).

  Downsampling does not need coplanarity. On the tesselation thread, after the engine has
  assembled a chunk's parts, the LOD 1 meshes of the Opaque, OpaqueNoCull, BlendNoCull and
  TopSoil parts go through one build: every axis-aligned unit face marks the block behind it
  solid and the block in front air; air floods through the unknown within the chunk (one row
  up into the padding, so a cell at the chunk's top has an air cell above it); what the
  flood does not reach is buried. A cell of 2x2x2 blocks is solid if any of its blocks is,
  else air if any is - so the picture is never thinner than the world, only up to a block
  fatter, which is what keeps neighbouring chunks at different tiers free of gaps. A solid
  cell faces every air neighbour with one face that copies the outermost source face of that
  direction in the cell: its tile, its four vertex lights, flags, colour map data, grass uv,
  index pattern and corner order, so winding and the SSBO face packing carry over and no
  shader is touched. Plants, leaves (cubes rotated about y), stairs, fences and slabs are
  rest faces: each cell keeps the block with the most of them, scaled by two about the
  cell's corner and floor, so a plant on the upper half of a cell stands on the fattened
  cell top instead of inside it. Tier 2 is the same build run on tier 1's output at twice the
  unit. Measured on a chunk with 6.462 faces (grass, soil sides, tall grass on two fifths of
  the columns, four trees): 1.820 faces at tier 1, 654 at tier 2, both tiers in about 1,2 ms
  (`./build.sh bench`, `far lod build`). The engine's own meshes are not modified: a part
  that has a picture is re-levelled so the sweep stops it at the distance, and the pictures
  ride on levels the engine's own range test never draws - with the sweep off, or the feature
  off, the parts are put back to level 1 and the pictures hidden, nothing re-tesselated. The
  shadow passes keep casting the engine's meshes; a picture never casts, a shadow at the
  player's feet would show its extra block.

  Tier 2 lives on the chunk's first centre part: the engine re-tesselates a chunk's two-block
  shell alone whenever a neighbour changes, and cells of four straddle shell and centre, so a
  shell-only pass leaves tier 2 alone (at four times the far distance a changed block in the
  shell is nothing anyone can see) and levels its new tier 1 pictures to stop where tier 2
  begins; a chunk with no centre part keeps tier 2 on its shell and rebuilds it with it. In a
  shell-only build the centre counts as unknown and the flood does not enter it, so no face
  is invented towards the centre's own picture.

  `FarMesh` (on), `FarMeshDistance` (0 = 0,35 of the view distance, at least 400 blocks: 538
  at view distance 1536, which puts 88 % of the visible area beyond it), `FarMeshTier2` (on,
  twice the distance); `.komet farmesh <blocks|off|on>`, `.komet toggle farmesh`,
  `.komet toggle farlod2` live. The report's `far lod` row prints faces in and out per build,
  the cost per chunk, the pictures in the pools and the triangles drawn per frame as tier 1,
  tier 2 and engine-within; the `camera pass by lod` line has the same three. Verified
  without the game: a plateau becomes exactly its 8x8 cell tops with the source's tile,
  light, flags and index pattern; twelve random rolling terrains produce exactly the cell
  picture computed from the heightmap alone, at both tiers, tops in the TopSoil part and
  sides in the Opaque part, each top copying the highest column of its cell; plants come
  out one per cell, doubled, standing on the cell top; a shell-only build invents no face
  towards the centre; the handover through the AddToPools prefix survives the engine's
  Dispose order and the levels (4, 5, 6, 7) come out as the hosting rules say; the sweep draws
  each level in its band and none of the pictures in either shadow pass, with the verifier
  agreeing. A watchdog switches the feature off if parts are handed to the pools and no
  picture is placed - the one failure that would end the world at the far distance.

  The merged far mesh and its shader variant are gone; this is what that machinery was for.

  **The first field report, and the three costs it found.** Same spot, same world, same
  settings, an hour and a half apart: at the horizon **53 fps with 16,1 million camera-pass
  triangles became 113 fps with 6,9 million**, the GPU frame 14,9 → 7,0 ms and the camera
  opaque pass, measured bottom-of-pipe, 11,7 → 4,2 ms. The far LOD does what it was built for.
  It also arrived with three costs that ate most of what it won, and all three are fixed here:

  - **The pictures shared pools with the engine's meshes.** First-fit interleaves them, so
    within the far distance every picture was an invisible part sitting between two visible
    engine parts, splitting the index runs the sweep merges — emitted ranges per raw range
    fell from 3,2 to 1,6 on a ground view and draw calls went 454 → 812 — and every pool held
    something visible in every view, so every pool cost a draw call. The routing that
    `SpatialPools` already had for regions now also has **lanes**: the engine's meshes, the
    tier 1 pictures and the tier 2 pictures get pools of their own, so a pool's parts are all
    visible in the same distance band. Costs at most a part-filled pool per lane per manager;
    the vertex budget, not the part limit, is what fills a pool (516 of 3000 parts in the
    field report), so there is no extra video memory in it.
  - **Every output mesh allocated a fresh int[].** The basic arrays come from the engine's
    recycler, but `MeshData.Dispose` nulls `CustomInts`/`CustomShorts` before handing a mesh
    back, so each of the two pictures per part needed a new one — the alloc sample put 31 MB/s
    of `Int32[]` on the tesselation thread and 241 of 276 hitches carried a GC pause. They now
    come from a size-class pool of the far LOD's own, the same kind the tight clone uses for
    the engine's extras. Measured in the bench against a real recycler: **45,6 KB allocated
    per chunk build without the pool, 0,8 KB with it.**
  - **Parts too small to be worth a picture still got two.** A dozen flowers in BlendNoCull
    cost two pool parts, two entries in every sweep and two draw ranges to save a handful of
    triangles. Under 96 faces a part is left out of the build and keeps drawing its own mesh
    at every distance — its blocks are then not in the coarse picture at all, which can only
    add faces to the neighbouring cells, never remove one.

  What the field report also says, and what it does not: the ground view lost about 10 %
  (189 → 170 fps) to the doubled pool population, which is what the lanes and the threshold
  are for; and at the horizon the camera pass is still 60 % of the GPU at **30 million
  fragments against 15 million before** — the cells are up to a block fatter than the blocks
  they replace, so the win moved from the front end into fill. The next lever there is the
  far distance itself (`.komet farmesh <blocks>`, default `max(400, 0,35 × view distance)` =
  538 at view distance 1536), which trades a visible one-block step at that range for the
  2,7 million triangles the engine still draws inside it.

- **`.komet shadowfoliagerange <blocks>`: how far leaves and plants cast a shadow.** Toggling
  foliage out of the shadow maps entirely took a settled frame from 6,27 ms to 4,40 ms — 160 to
  228 fps — because the near cascade shades **315 million** foliage fragments and the far one
  **250 million**, against 32 million in the whole camera pass. Nothing else in the frame is
  within an order of magnitude of that. The shadow maps are orthographic, so a grass tuft costs
  the same shadow texels at 250 blocks as at 20 and the fragments scale with the *area* the
  cascade covers: cutting the far cascade's 255 blocks to 100 leaves 15 % of them. The range is
  applied as a narrower axis-aligned band on the sweep — the engine's own test, tightened — for
  the foliage passes and no other, checked both ways so a solid pass can never be narrowed
  (that would be holes in the shade) and no pass can ever be widened. Off by default: tree
  leaves and grass tufts share a render pass and cannot be told apart at pool granularity, so a
  low range stops a distant forest shading itself. The report prints the range and the gpu row
  prices it within a minute.

  **Correction, from the second scene it was measured in: that finding was scene-specific.** At
  a sun elevation of 17 degrees the near cascade's world footprint stretches to 224 x 55 blocks
  and its foliage shaded 315 million fragments; at 61 degrees the same cascade is 121 x 55 and
  shades 43 million, for 0,5 ms. Shadow foliage is the frame's biggest item at a low sun and a
  short view distance, and a minor one otherwise. The range is a knob, not a default, and the
  report now prints both cascades' triangle counts so its effect is visible in one command
  instead of a minute of averaging.

- **`.komet toggle particleorphan`: rename the particle instance buffers instead of waiting for
  them.** 1.543 particles are three instance buffers of 48 KB in total, rewritten and drawn
  every frame — and the report measured **10,43 ms per frame inside `UpdateMesh`**, forty
  percent of a 25,8 ms frame. Moving 48 KB is microseconds, so that is a stall:
  `glBufferSubData` on a buffer the GPU may still be reading either waits for that draw to
  retire or makes the driver allocate a shadow copy. `glInvalidateBufferData` says the old
  contents no longer matter, and the driver hands out fresh storage instead of synchronising.
  Safe here for a reason worth stating: the pool writes `AliveCount` instances and the draw
  reads exactly `AliveCount`, so nothing ever reads the part invalidation discards. Off by
  default until a report prices it, and the particle row says which way it ran.

- **`.komet alloctrace [seconds]`: the process records its own allocations, with stacks.**
  The in-process sampler could name the thread and the type — "Int32[] on the tesselation
  thread" — and no more, because the runtime hands its allocation ticks to a listener without
  the stack. EventPipe keeps the stack with every tick, and a .NET process's diagnostics port
  accepts a session from the process itself, so the game now records itself for N seconds
  (GC keyword, verbose) into a `.nettrace` next to the logs, with a sidecar naming the OS
  threads. The repository's `alloctool` (`dotnet run --project alloctool -c Release --
  report <file>`) turns it into bytes by thread, by type, by the innermost method outside the
  runtime, and the top stacks per type; its `selftest` records the tool's own process and
  finds a churn site of known name. The diagnostics client library ships in the zip.

- **A performance window on `.komet`, and on Ctrl+F7.** Nineteen pages, one per question:
  overview, frametime, CPU, GPU, rendering, culling, entities, chunks, memory/GC,
  threads/jobs, caches, mods, hitches, profiler, toggles, config, stress test, conflicts,
  report. `.komet` with no argument used to print the counters; that text has not gone
  anywhere and is `.komet stats`, which is also what several of the pages are built from.
  Ctrl+F7 next to F7 (overlay) and Shift+F7 (mod overlay) — a third variant of a key this mod
  already owns rather than a fourth key, because the engine matches modifiers exactly and runs
  that pass before the modifier-ignoring one, and the keys that *look* free are not (F6 was
  tried and is a minimap macro).

  What it does NOT do is measure anything. Every row reads the same static the overlay reads,
  and where a block of rows already existed it is called rather than restated: the frame
  breakdown, the GC block and the world block came out of `DebugHud.Compose`, the nine komet
  blocks out of the HUD's extra section, and the overlay's text is unchanged to the byte
  (`./build.sh preview` prints both, and it is the same). Every button calls the method the
  chat command calls — the report, the conflict scan, the hitch reset, safemode, the counter
  reset, start and stop of the stress test. A window with its own sampling would be a second
  instrument to keep in agreement with the first, and the first is the one people paste into
  bug reports.

- **1 % and 0,1 % lows, and a live frametime graph.** The averages this mod has always shown
  hide exactly what people complain about: one 40 ms frame per second moves a 10 ms mean by a
  third of a millisecond. Every finished frame time now goes into a 2048-frame ring — one
  float store per frame, under the same warmup gate the hitch log uses — and the window reads
  the mean of the worst 1 % and the worst 0,1 % out of it, each with the frame rate it
  corresponds to, next to the median and the longest frame in the window. The graph above them
  shows the *shape*: a regular sawtooth is a cadence, an isolated spike is a hitch, a step is a
  scene change, and all three read identically as a number. Where the window has more samples
  than pixels a column shows the worst frame in its bucket, never the mean — a graph of spikes
  that averages them away is decoration. The ceiling is twice the 1 % low rather than the worst
  frame, so one world-join spike cannot flatten twenty seconds of playing into the bottom pixel
  row.

- **A three-way answer to "CPU or GPU?".** The overlay's GPU-LIMITED tag is the top half of it;
  what was missing is the other end. "Not GPU-limited" is not "CPU-limited": a frame that is
  neither is waiting on something that is not work — vsync, the frame limiter, a compositor —
  and that case has already cost this project one wrong conclusion (36 fps at 7 % CPU with the
  GPU just over the refresh budget). `FrameVerdict` says GPU LIMITED, CPU LIMITED, BALANCED or
  "not measured", uses the driver's own utilisation where the OS publishes one and the GL span
  against the frame where it does not, and names the page that answers the next question.

- **The stress test keeps its table.** The result used to live in the chat and in
  client-main.log, so a run that finished while the player was looking somewhere else was gone.
  `StressTest.LastReport` keeps the finished one and `LiveReport()` builds the table as it
  stands — the same arithmetic on fewer rounds, and the report prints the round count next to
  each spread, so a half-finished run reads as one. The window's stress page shows phase,
  round, slice, progress and the running slice's own mean while it works.

### Changed

- **The edge-repair sweep no longer rehashes the whole tesselation queue.** Every 50 ms, on the
  tesselation thread and under `dirtyChunksLock`, the sweep rotates `dirtyChunks` once to lift
  border repairs to the front. Rotating a `UniqueQueue` through its own API costs four hash
  operations per key — `Dequeue` takes it out of the backing `HashSet`, `Enqueue` puts it
  straight back — for keys that never leave the queue at all, and the queue this walks holds
  tens of thousands of them during exactly the chunk flood the sweep exists for. It now rotates
  the inner `Queue` and touches the set only for the handful of keys that really are promoted.
  Same queue, same order, same result. `./build.sh bench` prices it (`edge sweep rotation`,
  four runs): 4,6–5,1× at a short queue, **5,8–6,8× at the 45.000 the inflow brake was built
  for** — about 1,1 ms a sweep down to 0,18 ms, so 16–21 ms a second of tesselation-thread
  time, and the same reduction in how long the network thread waits to insert an arriving
  chunk. The fallback to the API
  path is still there for a game update that moves those fields, and verify drives *both*
  paths through the same assertions — the order asserts, the conservation fuzz, and a new one
  that the set and the queue still agree on what they hold. Counter-checked by dropping the
  set removal from the fast path: the test fails.

- **The neighbour prefetcher stopped re-walking the same queue front hundreds of times a
  second.** It looks 32 entries ahead and unpacks the 27 chunks around each, then sleeps 2 ms
  and does it again — but the tesselator consumes well under one chunk per pass, so two
  consecutive snapshots are all but identical. Nearly every pass was 32 `chunksLock`
  acquisitions and ~860 dictionary lookups for chunks it had already unpacked on the previous
  one, against a lock the tesselation thread takes for every neighbourhood it reads and the
  network thread for every chunk that arrives. It remembers the entries it has walked now, and
  naps 20 ms instead of 2 when a pass finds nothing new — a 32-deep runway at ~4 ms a chunk is
  over a hundred milliseconds, so a longer nap cannot exhaust it. The set is owned by the
  worker alone (a world change bumps an epoch it clears on, rather than another thread clearing
  a `HashSet` under an `Add`), and being wrong still costs only work that would have happened
  anyway: a chunk the pool repacks after we skipped it is unpacked by the tesselator, exactly
  as before this worker existed.

- **The window prebuilder predicts the chunk the tesselator will really mesh.** It used to take
  the front entry of the queue, and the front entry is regularly one `TesselateChunk` drops
  before it ever builds a window: a chunk that is missing, all air — at a tall view distance
  most of a column above the surface is — or not yet loaded from the server. Predicting one of
  those wasted the whole overlap twice over: the worker built nothing (`BuildWindow` bails on
  an empty centre) and the chunk the tesselator did reach paid the full ~1,2 ms window build.
  The prediction now steps over up to eight such entries, in one `chunksLock` acquisition,
  looking the key up in the chunk map directly — the queue key *is* the chunk key, which is
  what `SetChunkDirty` does before it enqueues, and both mark funnels are in the engine
  fingerprint so a change to that would fail the drift check rather than quietly mispredict.
  Being wrong is free in both directions: a chunk that stops being empty between the
  prediction and the pop just makes the tesselator build its own window, as it did before there
  was a prediction. The report's `window pipeline` row counts the entries stepped over, so
  whether this earns anything on a given machine is a number and not a claim.

- **The near shadow cascade stops drawing the half of its depth that cannot cast a shadow.**
  This is the near pass's own 15–18 ms of a 20 ms GPU frame, and the cause is one missing term.
  The engine extends the near shadow box *up-sun* by `50 + 50*|1-sunY| + 100` = 150–200 blocks,
  which is right — light space looks along the sun (`LookAt(eye = sunPosition, center = 0)`), so
  only geometry at a higher light-space z than a receiver can shade it, and `ShadowBox.update`
  raises `maxZ` alone. Then `loadOrthoModeMatrix` writes `2/width`, `2/height`, `-2/length` and
  **no translation**, and an ortho with no translation clips `|z| <= length/2` about the
  light-space origin: it uses the box's *length* and never learns where the box sits. Half of
  that extend therefore lands *down-sun* — ninety-odd blocks of world behind every receiver it
  could be tested against, drawn into the near map every frame, unable to darken anything.

  The fix is one term in the same matrix. The up-sun plane stays exactly where vanilla put it,
  so every occluder vanilla drew is still drawn and nothing about the picture changes; the
  down-sun plane moves up to the last receiver the near map can still serve, which
  `shadowcoords.vsh` fixes exactly — the near weight is `clamp(1 - (len/shadowRangeNear - 0.15)
  - edge terms, 0, 1)`, zero beyond `1.15 x` the cascade's range — plus room for the `z > 0.98`
  knee, which cuts the near map off with a `x100` ramp rather than a fade. Light space is a
  rotation, so that Euclidean bound bounds the light-space depth too. verify pins both halves
  of the claim: the up-sun plane never moves, and every receiver within `1.15 x` range stays
  inside and off the knee.

  How much it is turned out to be **6 %, not the 27 %** the first build of this entry promised,
  and the game's report said so within the hour (`volume 221 of 236 blocks deep`). The light
  view is `LookAt(eye = SunPosition, center = 0)`, and `ClientGameCalendar` sets `SunPosition =
  SunPositionNormalized * 50` — so the light-space origin the ortho centres on is not the camera
  but a point *fifty blocks up-sun* of it. Relative to the camera, vanilla's 236-block volume is
  68 blocks down-sun and 168 up-sun; the receivers need 45 of the 68, and the fit takes the
  rest. It stays, because it is correct, costs nothing, and is what makes `.komet
  shadowneardepth` safe (below).

  Everything downstream is built from the same matrix afterwards — the pushed `PMatrix`,
  `shadowMvpMatrix`, `toShadowMapSpaceMatrixNear`, and the six frustum planes
  `CalcFrustumEquations` derives from `PMatrix.Top` — so the shadow lookup and the CPU cull
  follow the new range on their own. Two things come free: `fogandlight.fsh` biases the near
  lookup by a constant `0.0005` in *normalised* depth, so a shorter box shrinks the world-space
  bias by the same factor (that bias is what peter-panning under foliage is made of), and the
  depth buffer spreads the same precision over less world. Far cascade deliberately untouched —
  same defect, but 2–6 ms amortised against 15–18, a retained and reprojected map, and a box
  already replaced wholesale. `ShadowNearDepthFit` in komet.json, `.komet toggle shadownearfit`
  live, and `.komet report` prints the fitted volume next to the engine's.

  It also makes `.komet shadowneardepth` *safe*, which it was not: the cap shortened the volume
  symmetrically, and with the volume 50 blocks up-sun of the camera, a cap of 80 put the
  down-sun plane 23 blocks below the camera — above receivers the near map serves out to 45.
  Those lost their near shadow on flat ground, exactly where the cap was supposed to be free.
  The fit derives the down-sun plane from the receivers and holds it there whatever the cap
  does, so the cap now only ever moves the up-sun end; verify checks the 80-block case. What
  the cap buys is also smaller than claimed: on flat ground the near pass draws the strip of
  terrain the tilted box meets, and that strip's length is the box's *height* over the sine of
  the sun's elevation — the depth only matters where terrain rises into the up-sun part of the
  box. A mountain up-sun, a cliff you stand under. The default is still the engine's.

- **The near shadow pass draws only what can shadow something on screen.** The near cascade's
  box does not follow the view — `ShadowBox.getCameraRotationMatrix` returns the identity, so it
  is a fixed world-axis shape around the player — and the map it fills serves receivers in every
  direction. The ground behind the camera is shadowed as carefully as the ground in front, and
  nobody samples it, because it is not on screen. Every caster whose shadow lands only there is
  drawn for nothing, and in a forest that is most of the casters behind you and beside you.

  What *can* reach a visible receiver is exact. The near map serves a receiver only within
  `1.15 x` its range (`shadowcoords.vsh`), only receivers in the view frustum are drawn, and a
  caster shades along the light direction only — in light space, the z axis. So a caster
  matters only if its light-space (x, y) is one some receiver in the frustum slice also has, and
  the shadow projection's four lateral clip planes are planes of constant light-space x and y.
  `ShadowFootprintPatches` pulls those four planes in to the slice's extent — on the planes the
  engine itself built in `PrepareForShadowRendering`, found by their normals being perpendicular
  to the light, each moved in by the minimum signed distance over the slice's five corners
  (convex region, linear distance), bounded again by the ball's centre minus its radius, less a
  pad of 8° plus twice the last frame's turn and 4 blocks. Vanilla's `InFrustumShadowPass` and
  `FastCuller` read the same plane fields, so both paths follow and the cull verifier stays
  valid. The map's coverage, texel grid and lookup do not change; the map holds "no caster"
  where nothing on screen could have read one. verify fires 54 sun/view combinations x 1500
  random sun rays through visible receivers at it: every caster vanilla kept on such a ray is
  still kept, the depth planes never move, and at least one view direction saves something.

  It steps aside when the near cascade is retained across frames (`.komet shadownearskip`): a
  retained map cut to one view would be wrong for the next. The far cascade is untouched for the
  same reason — its map *is* retained and reprojected, and a rotation is free for it precisely
  because it covers every direction. `ShadowNearFootprintCull` in komet.json, `.komet toggle
  shadowfootprint` live, and the report prints the footprint kept next to the near pass's
  triangles.

- **Mesh pools are places now, and the camera pass is drawn nearest first.** The engine's
  `MeshDataPoolManager.AddModel` takes the first pool with room, so every pool holds parts from
  all over the loaded world and every pool's draw covers the whole view — which is why the
  camera pass could never be drawn front-to-back: a far part of pool 1 comes before a near part
  of pool 2 whatever the parts inside are sorted by, and early depth rejection, which lets the
  GPU skip shading every fragment behind the first leaf canopy, had nothing to work with. The
  bottom-of-pipe probe put the camera pass at 39 million shaded fragments on 3,7 million pixels
  — ten per pixel, one kept — with the fragment shader about half of the pass.

  `SpatialPools` routes each model into a pool of its chunk's region (128 blocks per side, every
  height): the same sizing, registration and origin rule as the engine's, a new pool for the
  region when its pools are full, mini-dimension models left on the engine's path, and pools the
  reclaimer emptied dropped on the next miss. With a pool a place, its cached box is small, and
  `PoolPassPatches` sorts each manager's pool list by distance once per frame before the
  engine's loop reads it (camera pass only; the shadow passes are depth-only and keep their
  order). Inside a pool the sweep emits cells nearest first, each cell's parts in bucket order so
  the back-to-back merges inside a cell survive; gap bridging, which walks the list in index
  order between two emitted parts, is off in a sorted sweep. The cull verifier puts the emitted
  ranges back into byte order before comparing — the set is what it checks, and the set is
  unchanged. `SpatialPools`, `SpatialPoolRegion`, `FrontToBack` in komet.json, `.komet toggle
  spatialpools|fronttoback` live, and the report prints the order, the regions and how many
  pools they hold.

  **Both are off by default, and the report that decided it is worth keeping.** With 128-block
  regions at view distance 1536 the session made 1.917 pools of 56 parts each (first-fit: 513
  of 289), every one allocated at the engine's full pool size — four times the video memory,
  allocation stalls of 0,3 to 7,9 seconds while the driver paged, 21 fps. And the reason it was
  built did not happen: shaded fragments stayed at 40 million nearest first against 39 million
  in index order. The depth test does not reject early under the chunk shader — a shader that
  discards writes depth late, and the early test has nothing final to reject against — so
  draw order never reaches the fragment shader. A depth pre-pass would give it something
  final, and was priced against the same numbers: a second front end (~2,3 ms) plus trivial
  fragments to save ~1,8 ms of shading, a net loss in this scene. What is left is what the
  histogram said before the experiment: fewer triangles beyond 640 blocks, and nothing else.
  The code stays, gated off, as an experiment with a number on it; leaving the world drops
  the pools it made.

- **`.komet toggle flatfrag` swaps the chunk fragment shader for a trivial one, live.** The
  probe prices the camera opaque pass but cannot say whether that is the *fragment shader* —
  fog, two cascades' PCF, sky colour, effects — or the *front end* rasterising millions of
  sub-pixel triangles; the two want opposite work. The swap keeps the vertex shader and the
  engine's alpha test term for term (the same fragments survive, the depth buffer is the same)
  and writes the texel as is. It is done on the engine's own program object, the way the
  engine's shader reload does it: `Compile()` links a new program and rebuilds the uniform
  table, so every declaration above `main` is kept — a vanished uniform would make the next
  typed setter throw — and only the body changes; the orphaned GL ids are deleted. A
  diagnostic: safemode and leaving the world put the original back, an engine reload drops it
  on its own (the program instance changes), and verify checks the declarations survive.

- **The camera pass's triangles are booked by render pass, distance band and LOD level.** The
  first bottom-of-pipe report settled the near cascade at 1,3 ms (solid 0,1 + foliage 1,2) and
  priced the camera pass at 5,2 ms for six million triangles — 17 million in the forest report
  that started all this. Leaves have no LOD 2 stand-in (only the aquatic blocks carry
  `doNotRenderAtLod2`), so a forest is drawn leaf by leaf out to the view distance, sub-pixel
  and quad-overdrawn. Which pass, which distance and which LOD the triangles belong to is the
  question the next lever hangs on, so the sweep now books every emitted part's triangles into
  a (pass, band, lod) table — one add per part, thread-local, folded at the frame boundary.
  The pass comes from a prefix on `MeshDataPoolManager.Render` (`PoolPassPatches`) that names
  the manager by reference in `ChunkRenderer.poolsByRenderPass`; a pool never changes manager,
  so it is remembered on the pool's cache. Three report rows: by pass, by distance band with
  the foliage share, and by LOD.

- **`.komet foliagerange <blocks|off>`: the foliage passes' draw range.** A coarse lever, priced
  by the rows above: beyond the range the OpaqueNoCull (leaves, plants) and BlendNoCull passes
  are not drawn in the camera pass, so a tree there is a trunk. It is a cap on the LOD distance
  table of a foliage pool's sweep, so the sweep costs nothing extra, and the cull verifier is
  told to look away from those sweeps (they legitimately differ from vanilla). `FoliageRange` in
  komet.json, default 0 = vanilla; safemode switches it off.

- **The chunk passes are measured bottom-of-pipe, with fragment counts.** The per-stage GPU
  figures are timestamps, and a timestamp is written when the command processor *reaches* it,
  not when the work before it has finished: draw calls are dispatched in microseconds and
  complete later, and whichever span holds the next barrier — a framebuffer clear, a texture
  that was just rendered to being sampled — inherits everything still in flight. That is how a
  report came to say `near 17,4 | opaque 0,0` for a near pass of 593.438 triangles and a camera
  pass of 17 million, and three optimisations were aimed at the near cascade on the strength of
  that row before its triangle count was printed next to it. `GL_TIME_ELAPSED` ends when the
  enclosed commands have *completed*, and `GL_FRAGMENT_SHADER_INVOCATIONS` counts what the
  pass shaded — the number that settles "fill or geometry" without an argument. `GpuPassProbe`
  brackets the near and far cascades' solid and foliage halves (the transpiled boundary
  `ShadowCullPatches` already owns) and `ChunkRenderer.RenderOpaque`, on every third frame;
  only one elapsed-time query may be active, so on those frames the whole-frame query is not
  issued and keeps two of three samples. Results are read four probes later and only when the
  driver says they are ready. The report row: `gpu per pass (elapsed, every 3. frame): near
  solid X ms / N Mfrag, near foliage Y ms / M Mfrag | camera opaque Z ms / K Mfrag`.

- **`.komet toggle shadowfoliage` skips the foliage passes of both shadow maps.** A diagnostic,
  never a configuration: leaves, grass and crops stop casting, and the GPU row while it is on
  is the one-command answer to whether the shadow pass is paying for the foliage. A prefix on
  `MeshDataPoolManager.Render` that draws nothing between the transpiled boundary and the end
  of `RenderShadow`; safemode and a world change switch it off.

- **The particle row tells the physics from the upload.** `TickFixedStep` advances a particle
  only every `PhysicsTickTime` (a sixteenth of a second), so 660 particles cannot be 16 ms of
  physics — but `glBufferSubData` on an instance buffer the GPU is still reading blocks the
  render thread until the GPU has caught up, and a GPU-bound frame's wait shows up wherever the
  CPU next touches a busy buffer. `Platform.UpdateMesh` is bracketed while a main-thread pool's
  `OnNewFrame` runs; the row reads `physics X + upload Y`.

- **The report prints the near pass's own triangles.** `near pass: N triangles in M ranges per
  frame (camera pass K triangles) | footprint X %` — counted by the sweep per cull mode (one add
  per pool per sweep, in the thread-local block), so it is what was *submitted*, and the near
  cascade's GPU milliseconds can finally be read as ms per triangle against the camera pass. The
  pool's `RenderedTriangles` field could not say this: every sweep of the frame overwrites it,
  and the report read whichever mode ran last. The near-cascade row also names the sun's
  elevation, the number that sets how long a strip of ground the tilted near box cuts out.

- **`.komet shadowneardepth` caps the near cascade's depth.** Chasing the near cascade's 20 ms
  turned up the thing that actually sets its cost, and it is not the map size (4096 → 2048 px
  moved it by 6 %). The near pass culls against the shadow projection's own six planes —
  `PrepareForShadowRendering` feeds `CalcFrustumEquations` the ortho matrix and a look-at along
  the light — so what it draws is exactly the clip volume, and that volume's depth is the
  engine's `ShadowBoxZExtend`: `50 + 50*|1-sunY| + 100`, i.e. 150 to 200 blocks. That is *more*
  than the far cascade's own extend, for a cascade covering 39 blocks instead of 255. The near
  volume is therefore a 60-block-wide column of the world two hundred blocks deep, and in a
  forest that column is foliage from the ground to the top of every tree in it. The cap is
  roughly linear in what the pass draws. What it costs is occluders further up-sun than the cap:
  they stop casting into the near map and keep casting in the far one, so their shadow lands at
  half strength instead of full. Default is vanilla, because that is a judgement about a
  particular world — `ShadowNearDepthExtend` in komet.json, or the command, which prices it on
  the GPU page in seconds. `.komet report` prints the depth in use next to the engine's.
- **Particles have a number now.** They were the last part of the frame this mod could not name
  one for: render stages, tick listeners, renderers, uploads and culling were all measured, and
  "is it the particles?" could only be answered with an opinion. `SystemRenderParticles` calls
  `OnNewFrame` on the main-thread pools inside the Opaque and OIT stages, and for those pools
  that call is the whole physics step — `TickFixedStep` per particle, block collision included —
  plus the instance-buffer fill and its upload; the off-thread pools only pick up what their own
  thread produced. Both are timed, the report prints them apart with the live counts, and the
  price is eight `Stopwatch` reads per frame. There is deliberately no optimisation attached to
  it: the measurement comes first.

- **The shadow passes' cull pre-filter is cut to the box the projection keeps.** The engine
  derives the range from the shadow distance plus `ShadowBoxZExtend` — a depth-axis quantity,
  spent on the world X axis whatever the sun is doing — which for the near cascade is a 206–251
  block band against a 49-block box. `loadOrthoModeMatrix` writes no translation, so the clip
  volume is exactly ±width/2, ±height/2, ±length/2 in light space around the camera, and the
  world-axis box around *that* is what the range is now cut to, plus 48 blocks of slack: 72–161
  blocks instead of 206–251 across sun elevations 5–80°.
  
  What this saves is CPU, and the first version of this entry claimed otherwise. `InFrustum-
  ShadowPass` runs the range test and then six plane tests, and during a shadow pass those
  planes are not the camera's — `CalcFrustumEquations` is called with the ortho projection and a
  look-at along the light, so they already bound the clip volume exactly. The range is a
  pre-filter in front of them, so tightening it draws precisely the same geometry and spares the
  plane evaluations. The field measurement said so plainly: the near cascade did not move.
  It only ever narrows, the pad keeps it conservative against the plane test that actually
  decides, and verify pins that over 25 sun angles and three box shapes no point the projection
  keeps lies outside the range. `.komet toggle shadowclip`; `.komet report` prints the band next
  to vanilla's.

- **The runtime toggles are a table, not a switch statement.** Forty-eight `case` labels became
  forty-eight entries with a key, a group, a state reader and a flip that returns the very
  sentence the chat has always printed, and `.komet toggle` looks its argument up in it. The
  window draws the same table as switches, so a flip made in either place runs the same code
  and leaves the same line in the log. The list the command prints on an unknown name is built
  from the table too — the hand-written one had silently stopped covering `tightclone`,
  `extrapool` and `animcull`, which is the exact failure a second list in the GUI would have
  repeated. Nineteen of the entries are marked as changing what is *drawn*, and verify pins
  that set against what safemode switches: those are the rows a visual artefact is bisected
  among, and a window that marked the wrong ones would send somebody hunting in the wrong place.

- **`.komet mods` in the window does not rescan.** The chat reply still walks every loaded
  assembly's patches first, because a table nobody refreshed is a stale one. The window
  composes several times a second and reads what the ten-second scan listener already
  collected; its Rescan button forces one. The same reasoning put the chunk-renderer walk
  (`GetStats` plus `CalcFragmentation` over every mesh pool) behind one shared four-times-a-
  second sample for the whole window, and made it the overlay's sampler rather than a copy.

### Fixed

- **The switches page drew outside its own frame, and a blocked switch's reason drew across the
  row below it.** Two independent overruns, both from a page laid out for the size the window
  was designed at and drawn at whatever size the screen allows.

  Down: thirteen switches at a fixed 32-unit pitch start 44 units below the top and end 460
  down, and the content box's own floor is 443 — so at a small window or a high GUI scale the
  last switches and the entire message panel were drawn below the frame. Pitch, switch size and
  panel height now come out of the room there actually is, and eight group buttons wrap onto as
  many rows as their widest label needs instead of running off the right edge.

  Across: a switch that cannot be flipped on this machine appends its reason to the row, and the
  row was handed to the engine's static text element — which autobreaks to the box *width* and
  then keeps drawing past the box *height*. A sentence-long reason ("ShadowDistanceMultiplier is
  1.0 in komet.json — this switches between that and vanilla, and they are the same.") was drawn
  straight over the next switch's label. A row is one line now, cut to the cells its box holds
  with an ellipsis, and the whole sentence is still one click away in the panel under the rows —
  where it has always gone, and where it wraps properly.

  Verify walks every group at every window size the layout check already uses, times four
  button widths a translation might produce, and asserts the lot: buttons inside the content,
  switches not reaching into the row below them, labels inside their boxes, the panel neither
  overlapping the last row nor hanging out of the frame, and never shrinking below something
  readable. It was the toggle rows that escaped the existing "every page fits its panel" check,
  because they are composed as elements rather than rastered by `TextPanel`.

- **The particle measurement folded every frame twice and reported half of it.** Its frame-boundary
  handler was subscribed on world load and never unsubscribed on unload, so a second world in the
  same session stacked another one. `MeasurementPatches.FrameBoundary` is static and survives the
  world — the dispose path says so in a comment, and every other consumer is taken off there — and
  a doubled fold publishes the frame's call count and then immediately overwrites it with the
  zero of the second fold. The report showed the shape of exactly that: a non-zero cost per frame
  next to "the bracket is not running", with the cost itself about half of the truth because
  every other fold averaged in a zero. The handler comes off on dispose now, like the others.

- **The tightened shadow cull range left out the light eye's 50-block offset.** The range test
  measures from the player; the clip volume is centred on the frustum look-at's eye, which is
  `CameraPos + SunPosition`, and `SunPosition` is the direction times 50. The first version
  called that "the light matrix's own unit eye offset" and left it to the 48-block pad, which
  after the part radius has 20 to spare — at a 35° sun the offset is 41. A band of casters at the
  up-sun edge of the volume was range-culled that the planes would have kept: the long shadows of
  a hill up-sun, missing from the near map at half strength. The offset is its own term now
  (`ShadowPatches.EyeOffsets`), and verify places the eye where the game does — it fails without
  the term.

- **The particle row was wedged between an `if` and its body.** The animatable-gate row lost its
  condition and the particle row inherited it, so each printed under the other's rule.


- **One malformed animation no longer costs a creature its whole warm-up.** A field log from a
  1.22.5 client shows `game:locust-corrupt-sawblade` throwing on `idlesaw` ("QuantityFrames set
  to 7 but a key frame at frame 7"), and the loop that generates a shape's frames on the worker
  stopped there — so every animation *after* the bad one was left to the engine's lazy path on
  the main thread, which is the exact hitch the warm-up exists to remove, now paid in full
  because of one bad entry. The failing animation is skipped, left untouched so the engine's own
  lazy path still throws its own exception in its own place if it is ever played, and counted:
  the log says how much of the shape did warm up, and the HUD row carries `N malformed`.

  It matters that this warm-up does more than the engine does. The engine generates an
  animation's frames when that animation first *plays*, so malformed data on an animation
  nothing ever starts is data the engine never touches — generating everything up front finds
  it. That is a reason to skip the entry and carry on, not to stop.

### Internals

- **The window's text is rastered the way the overlay rasters its own**, not with the engine's
  text elements. `TextPanel` splits at the newlines the composer already put in and draws one
  `ShowText` per visible line into a surface it reuses; the engine's `TextDrawUtil` breaks
  lines by measuring word by word against the box, a cairo call per measurement each
  re-selecting the font face, which is what took the overlay off it in the first place.
  `./build.sh bench` now prices both on whatever machine runs it — 0,232 ms against 0,445 ms
  for a 34-line page here, so about half, not the order of magnitude a Windows tester once saw.
  Only the visible lines are drawn, so scrolling a thousand-line report costs what scrolling a
  ten-line one costs and the texture upload stays the size of the panel.
- **The pages are laid out for the panel, not for the overlay's box.** Every row this mod
  prints is pre-formatted monospace, and the writers were built for the F7 overlay — which
  sizes its box to the longest line it produced, so nothing there is ever too long. The
  window's panel cannot grow, and the same rows ran off its right border and were cut off
  mid-word by the surface edge: the tick-listener line lost its list of listeners, the
  main-thread line lost its budget figures, and a cut-off line does not look cut off, it looks
  broken. `TextPanel` now breaks overlong lines at the last space that fits (hard at the cell
  for a type name or a path, which has nowhere to break) and indents the continuation, so a
  wrapped row still reads as one row — a character count against a monospace cell, not the
  engine's word-by-word measuring: 4 us for a forty-row page here, and only when the text or
  the panel's width in cells changes. The row geometry itself is ambient now: the section rules
  span the panel instead of stopping at the overlay's 48 columns, and the label column is wide
  enough for a renderer's profiling name — cut to thirteen, three different renderers all read
  `Before-Sheyde`, which looks like the same renderer counted three times. What is cut is
  marked, and the stage the engine's profiling name already begins with is no longer repeated
  in a column after it — in the F7 overlay's copy of that table too, because it is the same
  code and the column was redundant in both. The overlay keeps its own geometry (13/9/48) to
  the byte, and verify pins the rest: that no page draws a line wider than the panel, and that
  no word is lost in the break.
- **The window's bounds tree is built in one place, and checked without a screen.** Two things
  were wrong with it, and both looked like rendering faults rather than layout ones.
  `ForkBoundingParent` does not only return a parent — it *moves* the bounds it is called on
  into that parent — so the scrollbar beside the inset, the buttons under it and the text panel
  inside it, all derived from the inset before the fork, kept the position the frame had
  beforehand: the whole page was drawn 43 units above and 8 left of its own frame, with its
  first line behind the title bar and its last below the inset border. And the content was a
  constant 860×528, which needs a window of 1079×650 unscaled units once the chrome and the tab
  column are counted; below that the dialog did not shrink, it hung over the edges, and the
  scrollbar, the close button and the title bar's own buttons were the first things gone.
  The content is now what the window has room for, up to the size it was designed at, rebuilt
  when the window is resized or the GUI scale changes; the tab strip is sized from the tab count
  rather than from the content, so a shorter page cannot cut the last tabs off. `BuildLayout`
  is pure and static, and verify builds the same tree against a stand-in screen at eight window
  sizes and scales and checks what a screenshot would have had to show: panel and graph inside
  the inset, scrollbar beside it, buttons under it, tab strip inside the dialog's height, and
  the whole footprint inside the screen.
- **The patch guard scans in slices.** With the listeners named (below), a field hitch log
  named the culprit on the first try: `KometModSystem.PatchGuardTick 12,6 ms`, on the render
  thread, every ten seconds. The cost is per patched METHOD — Harmony keeps a method's patch
  info serialised and rebuilds it on every `GetPatchInfo`, so ~150 patched methods are ~150
  deserialisations — and none of it has to happen in one frame: the guard exists to notice a
  lazily applied patch eventually, not within a frame. The periodic path now walks two
  milliseconds' worth per tick and publishes when it reaches the end. The world-join scan and
  `.komet conflicts` still scan whole, because there the answer is wanted at once, and verify
  pins that a scan sliced one method at a time finds exactly the collisions the whole one does.
  The mod inventory scan, which walks the same registry at the same price, drops to once a
  minute after the first three.
- **`.komet shadownearskip` sets the near cascade's cadence live.** The far cascade has been
  throttled since 1.43.0 and the near one has not, because it covers the ground right around the
  player. When a GPU report puts the near cascade at twenty of twenty-four milliseconds, halving
  how often it is drawn is the largest single number on the table — and the retained map is
  reprojected exactly for camera movement, so what goes stale is only what moved. One command
  instead of a config edit and a restart.
- **Komet's own periodic listeners can be told apart.** The tick profiler and the hitch log
  name a listener after the method its delegate belongs to, and six of komet's were lambdas
  written inside `StartClientSide` — so the inflow brake, the renderer re-wrap, the edge flush,
  the stats log, the mod inventory scan and the patch guard all landed in one bucket called
  `KometModSystem.StartClientSide()`. A field log shows that bucket at 10–16 ms every ten
  seconds with no way to say which of the six it was, which is the one thing this mod's own
  instrument must not do to itself. They are named methods now, so the next log names the
  culprit. The patch guard's cadence also follows what it is looking for: mods patch lazily, on
  first use and on world join, so it scans every ten seconds for the first three minutes and
  once a minute after that instead of walking Harmony's whole registry every ten seconds for
  a set that has not changed since the world loaded.
- **The window pays for its own raster, and stops stalling on it.** A field log has the Ortho
  stage at 10-17 ms per refresh with the window open and 1,2 ms of it booked as "hud" — the
  gap was the texture upload. `glTexSubImage2D` out of the cairo surface cannot start while the
  GPU is still reading that texture for a frame in flight, and on a GPU at 80 % busy the render
  thread waits for it. Two changes: the panel writes into whichever of two textures was not the
  one drawn last, so the upload has a full refresh of slack (1,7 MB of video memory for it), and
  the raster's time is now booked to `FrameStats.AddHudMs` and fed into the refresh cadence like
  everything else. The window used to read its own price as the 1-2 ms of composing a page and
  conclude it was cheap; it now backs off from what it actually costs.
- **Two toggles that reported success and did nothing.** `.komet toggle shadowdist` switches
  between vanilla and whatever `ShadowDistanceMultiplier` says — with that left at its default
  of 1,0 the two sides are the same, so it flipped, printed "shadow distance x1 (vanilla)" and
  changed nothing (a field log has twelve of those in a row, which is what a player does when a
  switch says it worked). It is now marked unavailable with the reason, and the window greys the
  row out instead of offering a switch that cannot move. `shadowstab` had the mirror image: it
  decided whether its patch was installed from `StatSnaps == 0`, which is also true for the first
  frame after switching it ON, so turning it on reported "patch not installed" on a build where
  it was installed and about to work. The patch now records that it applied.
- **What the window costs is printed in the window**, and added to `FrameStats.AddHudMs`, so a
  frame it spikes is booked to the overlay column of the hitch log instead of disappearing into
  "outside the stages". Its refresh interval adapts through the same rule the overlay's does
  (`DebugHud.NextIntervalSeconds`), so it spends a few percent of wall time on itself whatever
  the hardware. Closed, it has no renderer, no listener and no sampling.
- Four new checks: every historical `.komet toggle` name still resolves and flips back, the
  tail means and the graph's bucketing, the verdict's boundaries, and every one of the nineteen
  pages composing twice without a world behind it. A fifth guards the language files against a
  `Loc.Hud` label landing on a key some `Loc.T` already owns — it found one that would have
  printed a sentence where a section heading belonged, and nothing about the key sets looked
  wrong.
- `./build.sh preview` prints every page as text, which is how the layout is reviewed without a
  world, a GPU and forty mods installed.

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