using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Komet;
using Komet.Patches;
using Komet.Measure;

// Applies the real Harmony patches to the real game assemblies outside the game and checks
// that (a) each patch applies, (b) the patched IL actually JITs, (c) behaviour is unchanged.

internal static class Program
{
    private static int failures;

    private static void Check(string what, Action a)
    {
        try { a(); Console.WriteLine($"  ok    {what}"); }
        catch (Exception e) { failures++; Console.WriteLine($"  FAIL  {what}\n        {e.GetType().Name}: {e.Message}"); if (Environment.GetEnvironmentVariable("KOMET_TRACE") != null) Console.WriteLine(e.StackTrace); }
    }

    /// <summary>Forces the JIT to compile the patched body; invalid IL surfaces here.</summary>
    private static void ForceJit(MethodBase m) => RuntimeHelpers.PrepareMethod(m.MethodHandle);

    /// <summary>
    /// Writes the shipped default config from the very class the game loads. Doing it from the
    /// real type is the point: a dist/komet.json maintained by hand drifts from KometConfig the
    /// first time a default changes, and then documents a mod that does not exist.
    /// </summary>
    private static int WriteDefaultConfig(string path)
    {
        var cfg = new KometConfig { ConfigVersion = KometConfig.Current };
        string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(path,
            Newtonsoft.Json.JsonConvert.SerializeObject(cfg, Newtonsoft.Json.Formatting.Indented) + "\n");

        int settings = 0;
        foreach (PropertyInfo p in typeof(KometConfig).GetProperties())
            if (p.CanRead && p.CanWrite && p.Name != nameof(KometConfig.ConfigVersion)) settings++;
        Console.WriteLine($"wrote {path}: {settings} settings, layout {KometConfig.Current}");
        return 0;
    }

    private static int Main()
    {
        string[] argv = Environment.GetCommandLineArgs();
        if (argv.Length > 1 && argv[1] == "config")
            return WriteDefaultConfig(argv.Length > 2 ? argv[2] : "dist/komet.json");

        // The strict tests compare byte-identical against vanilla; gap bridging deviates on
        // purpose and has its own test, which flips this on locally and restores it.
        FastCuller.GapMergeDrawRanges = false;

        var harmony = new Harmony("komet.verify");

        Console.WriteLine("applying patches:");

        Check("MeshDataPool prefix/postfixes", () =>
            harmony.CreateClassProcessor(typeof(MeshDataPoolPatches)).Patch());

        Check("AnimatorBase ctor postfix", () =>
            harmony.CreateClassProcessor(typeof(AnimatorBaseCtorPatch)).Patch());

        var dropLower = new HarmonyMethod(AccessTools.Method(typeof(AnimationPatches), nameof(AnimationPatches.DropToLowerInvariant)));
        MethodInfo onFrame = AccessTools.Method(typeof(AnimatorBase), nameof(AnimatorBase.OnFrame));
        MethodInfo getState = AccessTools.Method(typeof(AnimatorBase), nameof(AnimatorBase.GetAnimationState));
        MethodInfo onClientFrame = AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame));

        Check("AnimatorBase.OnFrame transpiler", () => { harmony.Patch(onFrame, transpiler: dropLower); ForceJit(onFrame); });
        Check("AnimatorBase.GetAnimationState transpiler", () => { harmony.Patch(getState, transpiler: dropLower); ForceJit(getState); });

        var replaceAny = new HarmonyMethod(AccessTools.Method(typeof(AnimationPatches), nameof(AnimationPatches.ReplaceAnyWithLoop)));
        Check("AnimationManager.OnClientFrame transpiler", () => { harmony.Patch(onClientFrame, transpiler: replaceAny); ForceJit(onClientFrame); });

        Check("GlErrorPatches (opt-in)", () => GlErrorPatches.Apply(harmony));

        // Every batched-cull check below has to go through the real worker threads, not the
        // inline fallback - otherwise the parallel path ships unexercised.
        FastCuller.StartWorkers();

        Check("ChunkCuller prefix", () =>
        {
            FastChunkCuller.EnsureReady();
            harmony.CreateClassProcessor(typeof(ChunkCullerPatches)).Patch();
        });

        Check("mesh upload prefixes", () => MeshUploadPatches.Apply(harmony));

        // Applied here, but Enabled stays false globally: with the gate open, every
        // Recyclable MeshData.Dispose() anywhere in this suite would detour into the pool.
        // Only the dedicated test below opens it, against its own recycler instance.
        Check("mesh recycler prefixes", () => MeshRecyclerPatches.Apply(harmony));

        MethodInfo triggerStage = AccessTools.Method(typeof(Vintagestory.Client.NoObf.ClientMain),
            nameof(Vintagestory.Client.NoObf.ClientMain.TriggerRenderStage),
            new[] { typeof(Vintagestory.API.Client.EnumRenderStage), typeof(float) });
        Check("shadow fade fix + distance scaling", () =>
        {
            ShadowPatches.Apply(harmony, fadeFix: true, distanceMultiplier: 1.5, symmetricBox: true);
            MethodInfo far = AccessTools.Method(typeof(Vintagestory.Client.NoObf.SystemRenderShadowMap), "OnRenderShadowFar", new[] { typeof(float) });
            MethodInfo prep = AccessTools.Method(typeof(Vintagestory.Client.NoObf.SystemRenderShadowMap), "PrepareForShadowRendering",
                new[] { typeof(double), typeof(Vintagestory.API.Client.EnumFrameBuffer), typeof(float) });
            ForceJit(far);
            ForceJit(prep);

            // the far cascade is scaled, the near one must be left alone
            double d = 255;
            ShadowPatches.ScaleDistance(ref d, Vintagestory.API.Client.EnumFrameBuffer.ShadowmapFar);
            if (Math.Abs(d - 382.5) > 0.001) throw new Exception($"far not scaled: {d}");
            d = 39;
            ShadowPatches.ScaleDistance(ref d, Vintagestory.API.Client.EnumFrameBuffer.ShadowmapNear);
            if (Math.Abs(d - 39) > 0.001) throw new Exception($"near was scaled: {d}");

            // Runtime gating (1.37.0): the patches are always installed, so every shadow
            // behaviour must be switchable back to vanilla mid-session - this is what makes a
            // shadow artefact bisectable while it is on screen instead of one restart per
            // guess. Counter-checked by ignoring the flag in ScaleDistance: the vanilla
            // assert below then fails.
            ShadowPatches.ToVanilla();
            if (ShadowPatches.SymmetricBox || ShadowPatches.FadeFix)
                throw new Exception("safemode did not hand the shadow shape back to vanilla");
            d = 255;
            ShadowPatches.ScaleDistance(ref d, Vintagestory.API.Client.EnumFrameBuffer.ShadowmapFar);
            if (Math.Abs(d - 255) > 0.001) throw new Exception($"distance still scaled after ToVanilla: {d}");

            ShadowPatches.ToConfigured(symmetricBox: true, fadeFix: true);
            if (!ShadowPatches.SymmetricBox || !ShadowPatches.FadeFix)
                throw new Exception("leaving safemode did not restore the configured shadow patches");
            d = 255;
            ShadowPatches.ScaleDistance(ref d, Vintagestory.API.Client.EnumFrameBuffer.ShadowmapFar);
            if (Math.Abs(d - 382.5) > 0.001) throw new Exception($"multiplier not restored from config: {d}");

            ShadowPatches.ToVanilla();
        });

        Check("symmetric shadow box is exactly as big as shadowcoords.vsh needs", () =>
        {
            // The property, taken straight off the shader rather than from a remembered radius.
            // shadowcoords.vsh gives the far cascade a weight of clamp(1.5 - 2d, 0, 1) with
            //     d = clamp(uvEdge * 10 + max(0, len / shadowRangeFar - 0.15), 0, 1)
            // so the shadow is fully faded at d = 0.75, i.e. at len = 0.90 * shadowRangeFar when
            // the UV terms are zero - and they are zero only inside uv [0.03, 0.97], the middle
            // 94 % of the box. So:
            //
            //   REQUIRED: every point within 0.90 R of the camera lands inside the middle 94 %.
            //   FORBIDDEN: anything larger, because every extra block of light-space area is
            //              texel density lost and, at +0,72 ms measured, frame time spent.
            //
            // Both halves matter. Covering more than this is what the box did until 1.42.1
            // (half-size R, so 4,3 % too large per axis for 8 % of wasted area).
            double[] lightView = new double[16];
            Vintagestory.API.MathTools.Mat4d.LookAt(lightView,
                new double[] { 0.38, 0.85, 0.36 }, new double[4], new double[] { 0, 1, 0 });

            const double camX = 1234.5, camY = 143.2, camZ = -987.3, range = 382.5;
            double half = range * ShadowPatches.BoxRadiusFactor;
            double faded = range * ShadowPatches.FadeCompleteFraction;

            ShadowPatches.SymmetricLightSpaceBounds(lightView, camX, camY, camZ, half,
                out double minX, out double minY, out double minZ,
                out double maxX, out double maxY, out double maxZ);

            var rnd = new Random(7);
            const double eps = 1e-9;
            for (int i = 0; i < 4000; i++)
            {
                // half the samples sit exactly on the fade sphere - the points whose shadow the
                // vanilla box cuts - and half anywhere inside it
                double a = rnd.NextDouble() * Math.PI * 2, b = Math.Acos(2 * rnd.NextDouble() - 1);
                double rad = (i & 1) == 0 ? faded : faded * Math.Cbrt(rnd.NextDouble());
                double px = camX + rad * Math.Sin(b) * Math.Cos(a);
                double py = camY + rad * Math.Cos(b);
                double pz = camZ + rad * Math.Sin(b) * Math.Sin(a);

                double lx = lightView[0] * px + lightView[4] * py + lightView[8] * pz + lightView[12];
                double ly = lightView[1] * px + lightView[5] * py + lightView[9] * pz + lightView[13];
                double lz = lightView[2] * px + lightView[6] * py + lightView[10] * pz + lightView[14];

                // not merely inside the box: inside the band where the UV edge terms are zero
                double u = (lx - minX) / (maxX - minX), v = (ly - minY) / (maxY - minY);
                if (u < 0.03 - eps || u > 0.97 + eps || v < 0.03 - eps || v > 0.97 + eps)
                    throw new Exception($"point {i} at |d|={rad:0.0} lands at uv ({u:0.000}, {v:0.000}) - "
                                      + "inside the box but where the shader's edge terms already cut");
                if (lz < minZ - eps || lz > maxZ + eps)
                    throw new Exception($"point {i} escapes the box in depth");
            }

            // The box must be centred on the camera: loadOrthoModeMatrix writes only the scale
            // terms, so a lost translation column would shift it and shrink one side's coverage.
            double ccx = lightView[0] * camX + lightView[4] * camY + lightView[8] * camZ + lightView[12];
            double ccy = lightView[1] * camX + lightView[5] * camY + lightView[9] * camZ + lightView[13];
            if (Math.Abs((minX + maxX) / 2 - ccx) > 1e-6 || Math.Abs((minY + maxY) / 2 - ccy) > 1e-6)
                throw new Exception("box is not centred on the camera in light space");

            // TIGHTNESS. Two separate ways of being too large, both of which have shipped:
            // (a) the sqrt(3) hull of the cube [-r, r]^3 instead of the sphere's own bounds
            //     (fixed 1.39.0), (b) sizing the half-extent at R rather than at what the fade
            //     actually reaches (fixed 1.42.1). Anything above 2 * half is one of the two.
            foreach ((string axis, double span) in new[]
                     { ("x", maxX - minX), ("y", maxY - minY), ("z", maxZ - minZ) })
                if (span > 2 * half + 1e-6)
                    throw new Exception($"{axis} span {span:0.0} exceeds {2 * half:0.0} - box larger than it needs to be");

            // and the shrink has to be real, not a rounding artefact
            if (half > range * 0.98)
                throw new Exception($"half-size {half:0.0} is barely under the range {range:0.0} - the shader-derived shrink is not happening");

            // for a light along a cube diagonal the old hull was genuinely sqrt(3) larger,
            // so the sphere-bounds form is not a cosmetic change
            double[] diagLight = Mat4d.Create();
            Mat4d.LookAt(diagLight, new double[] { 0.577, 0.577, 0.577 }, new double[4], new double[] { 0, 1, 0 });
            ShadowPatches.SymmetricLightSpaceBounds(diagLight, camX, camY, camZ, half,
                out double dMinX, out _, out _, out double dMaxX, out _, out _);
            if (dMaxX - dMinX > 2 * half + 1e-6)
                throw new Exception("diagonal light still produces an oversized box");
        });

        Check("sphere box touches ONLY the far cascade's box", () =>
        {
            // The 1.42.x version widened both cascades. For the near one that bought nothing
            // (where its map ends, the far map takes over seamlessly - it has a safety net)
            // and cost half its texel density, the sharpness of every shadow near the player.
            // The cascade is identified by the ScaleDistance prefix, which is the one place
            // that sees which framebuffer is being prepared; this pins the whole chain.
            var camera = (Vintagestory.Client.NoObf.Camera)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.Camera));
            camera.OriginPosition = new Vec3d(1000, 120, -500);

            var box = (Vintagestory.Client.NoObf.ShadowBox)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ShadowBox));
            AccessTools.FieldRefAccess<Vintagestory.Client.NoObf.ShadowBox, Vintagestory.Client.NoObf.Camera>("camera")(box) = camera;
            double[] lightView = Mat4d.Create();
            Mat4d.LookAt(lightView, new double[] { 0.4, 0.8, 0.45 }, new double[4], new double[] { 0, 1, 0 });
            box.lightViewMatrix = lightView;

            bool savedBox = ShadowPatches.SymmetricBox;
            double savedDist = Vintagestory.Client.NoObf.ShadowBox.SHADOW_DISTANCE;
            try
            {
                ShadowPatches.SymmetricBox = true;
                Vintagestory.Client.NoObf.ShadowBox.SHADOW_DISTANCE = 36.0; // the near cascade's size
                double d = 36.0;

                // near cascade prepared -> the box postfix must not touch a single field
                box.minX = 1; box.maxX = 2; box.minY = 3; box.maxY = 4; box.minZ = 5; box.maxZ = 6;
                ShadowPatches.ScaleDistance(ref d, EnumFrameBuffer.ShadowmapNear);
                ShadowPatches.MakeBoxSymmetric(box);
                if (box.minX != 1 || box.maxX != 2 || box.minY != 3 || box.maxY != 4 || box.minZ != 5 || box.maxZ != 6)
                    throw new Exception("the near cascade's box was modified - its texel density is being halved again");

                // far cascade prepared -> the sphere bounds must land
                Vintagestory.Client.NoObf.ShadowBox.SHADOW_DISTANCE = 255.0;
                d = 255.0;
                ShadowPatches.ScaleDistance(ref d, EnumFrameBuffer.ShadowmapFar);
                ShadowPatches.MakeBoxSymmetric(box);
                double wantSpan = 2 * 255.0 * ShadowPatches.BoxRadiusFactor;
                if (Math.Abs(box.Width - wantSpan) > 1e-6 || Math.Abs(box.Height - wantSpan) > 1e-6)
                    throw new Exception($"far box spans {box.Width:0.0}x{box.Height:0.0}, expected {wantSpan:0.0} - the sphere did not land");
                if (box.Length <= wantSpan) // ZExtend must have been added to the depth
                    throw new Exception("far box depth is missing the ShadowBoxZExtend headroom for casters toward the sun");
            }
            finally
            {
                ShadowPatches.SymmetricBox = savedBox;
                Vintagestory.Client.NoObf.ShadowBox.SHADOW_DISTANCE = savedDist;
                double restore = savedDist;
                ShadowPatches.ScaleDistance(ref restore, EnumFrameBuffer.ShadowmapFar);
            }
        });

        Check("what the symmetric box really costs in texel density", () =>
        {
            // The config claimed "roughly 2.5x the light-space area, about 1.6x coarser per
            // axis" and derived it from vanilla's far plane being 0.78 R wide - which is a VIEW
            // space number, while the shadow box is the AABB of the eight frustum corners AFTER
            // the light transform. That looked like an overstatement worth correcting. Measured,
            // it is not: vanilla's span is 257 blocks with the sun at 5 deg, ~450 at 45-65 deg
            // and 397 in the zenith, against the sphere box's constant 510. So the sphere box is
            // 1,48x wider per axis at a typical sun and 1,99x at sunrise, and the estimate the
            // documentation carried was close to right.
            //
            // This test exists to keep that number honest, because it is what decides how many
            // ShadowMapExtraQuality steps the box has to be paid for with - and because the
            // temptation to argue it down rather than compute it has already been acted on once.
            const double r = 255.0, znear = 0.3, fov = 70.0, aspect = 16.0 / 9.0;
            double worst = 0, worstElev = 0, worstTypical = 0;

            for (int e = 5; e <= 90; e += 5)
            for (int a = 0; a < 360; a += 15)
            {
                double elev = e * Math.PI / 180.0, azi = a * Math.PI / 180.0;
                double[] lightView = Mat4d.Create();
                Mat4d.LookAt(lightView,
                    new[] { Math.Cos(elev) * Math.Cos(azi), Math.Sin(elev), Math.Cos(elev) * Math.Sin(azi) },
                    new double[4], new double[] { 0, 1, 0 });

                double vanillaSpan = VanillaBoxSpan(lightView, 0, 140, 0, r, znear, fov, aspect);

                // the box as MakeBoxSymmetric really builds it, shader-derived shrink included
                ShadowPatches.SymmetricLightSpaceBounds(lightView, 0, 140, 0, r * ShadowPatches.BoxRadiusFactor,
                    out double sMinX, out double sMinY, out _, out double sMaxX, out double sMaxY, out _);
                double sphereSpan = Math.Max(sMaxX - sMinX, sMaxY - sMinY);

                double ratio = sphereSpan / vanillaSpan;
                if (ratio > worst) { worst = ratio; worstElev = e; }
                if (e >= 30 && ratio > worstTypical) worstTypical = ratio;
            }

            // Bounds a little above the measured values, so an engine change to the frustum or
            // the cascade distance shows up here rather than as "shadows got blurry".
            if (worstTypical > 1.62)
                throw new Exception($"sphere box is {worstTypical:0.00}x vanilla's span at a normal sun elevation "
                                  + "(measured 1,48-1,59) - ShadowMapExtraQuality no longer pays for it");
            if (worst > 2.10)
                throw new Exception($"sphere box is {worst:0.00}x vanilla's span at {worstElev:0} deg elevation "
                                  + "(measured 1,99 at 5 deg)");

            // The two conclusions this feeds, as assertions rather than comments.
            //
            // (a) A tripwire, not a requirement: at ONE resolution step the sphere box is still
            //     coarser than vanilla. That is half the reason it is not the default (the other
            //     half is the measured +0,72 ms). If an engine change ever made this fall below
            //     1,0 the box would cost nothing in sharpness and the default is worth revisiting
            //     - so failing here means "come back and think", not "something is broken".
            double sphereAtOneStep = worstTypical / (7168.0 / 6144.0);
            if (sphereAtOneStep < 1.0)
                throw new Exception($"the sphere box is now only {sphereAtOneStep:0.00}x vanilla at one resolution "
                                  + "step - it no longer costs sharpness, so SymmetricShadowBox's default deserves a second look");

            // (b) What turning it on costs: two steps (8192, 1,33x) has to bring the normal case
            //     back to within about a fifth of vanilla, or ShadowMapExtraQuality = 2 is not a
            //     useful recommendation for anyone who wants the smooth fade.
            double afterTwoSteps = worstTypical / (8192.0 / 6144.0);
            if (afterTwoSteps > 1.20)
                throw new Exception($"even at 8192 the box is {afterTwoSteps:0.00}x coarser than vanilla");
        });

        Check("frame + render stage measurement", () =>
        {
            MeasurementPatches.Apply(harmony);
            ForceJit(triggerStage);
            // the swap transpiler throws inside Apply when it cannot find the call; force-JIT
            // the rewritten method so broken IL fails here instead of at the first frame
            ForceJit(AccessTools.Method(typeof(Vintagestory.Client.NoObf.ClientPlatformWindows), "window_RenderFrame"));
        });

        Check("stage timing buckets fill and roll over", () =>
        {
            FrameStats.Reset();
            long t = System.Diagnostics.Stopwatch.Frequency / 1000; // 1 ms worth of ticks
            for (int i = 0; i < 250; i++)
            {
                FrameStats.BeginFrame();
                FrameStats.AddStageTicks((int)Vintagestory.API.Client.EnumRenderStage.Opaque, t * 4);
                FrameStats.AddStageTicks((int)Vintagestory.API.Client.EnumRenderStage.ShadowFar, t);
                FrameStats.AddGameTickTicks(t * 2);
                System.Threading.Thread.Sleep(1);
            }
            FrameStats.BeginFrame();
            if (!FrameStats.HasData) throw new Exception("no data");
            double opaque = FrameStats.StageMs[(int)Vintagestory.API.Client.EnumRenderStage.Opaque];
            if (opaque < 3.5 || opaque > 4.5) throw new Exception($"opaque bucket {opaque:F2} ms, expected ~4");
            if (FrameStats.GameTickMs < 1.5 || FrameStats.GameTickMs > 2.5) throw new Exception($"game tick {FrameStats.GameTickMs:F2} ms, expected ~2");
            // buckets must not leak between frames
            if (FrameStats.StageMs[(int)Vintagestory.API.Client.EnumRenderStage.OIT] != 0) throw new Exception("unused bucket is not zero");
            FrameStats.Reset();
        });

        Check("worst frame breakdown names the spike, not the average", () =>
        {
            // 40 quiet 5 ms frames, then one 50 ms frame that spends 30 ms in the shadow far
            // stage, 8 ms in the game tick and carries a 6 ms GC pause. "schlechtester" must
            // report that frame's accounting - the smoothed averages stay near the quiet
            // frames, which is exactly why the breakdown has to be a snapshot.
            FrameStats.Reset();
            long msTicks = System.Diagnostics.Stopwatch.Frequency / 1000;
            long clock = 123456789;
            double gcTotal = 100;
            int far = (int)Vintagestory.API.Client.EnumRenderStage.ShadowFar;
            int opaque = (int)Vintagestory.API.Client.EnumRenderStage.Opaque;

            void Frame(long lengthMs, long opaqueMs, long farMs, long tickMs, double gcMs, long swapMs = 0)
            {
                FrameStats.AddStageTicks(opaque, opaqueMs * msTicks);
                FrameStats.AddStageTicks(far, farMs * msTicks);
                FrameStats.AddGameTickTicks(tickMs * msTicks);
                FrameStats.AddSwapTicks(swapMs * msTicks);
                gcTotal += gcMs;
                clock += lengthMs * msTicks;
                FrameStats.Advance(clock, gcTotal);
            }

            FrameStats.Advance(clock, gcTotal); // establish the frame start
            for (int i = 0; i < 40; i++) Frame(5, 2, 1, 1, 0);
            Frame(50, 2, 30, 8, 6, swapMs: 4);
            for (int i = 0; i < 10; i++) Frame(5, 2, 1, 1, 0);

            if (Math.Abs(FrameStats.MaxFrameMs - 50) > 0.5)
                throw new Exception($"worst frame {FrameStats.MaxFrameMs:F1} ms, expected 50");
            if (Math.Abs(FrameStats.WorstStageMs[far] - 30) > 0.5)
                throw new Exception($"worst shadow far {FrameStats.WorstStageMs[far]:F1} ms, expected 30 - averaged instead of snapshotted?");
            if (Math.Abs(FrameStats.WorstGameTickMs - 8) > 0.5)
                throw new Exception($"worst game tick {FrameStats.WorstGameTickMs:F1} ms, expected 8");
            if (Math.Abs(FrameStats.WorstGcPauseMs - 6) > 0.5)
                throw new Exception($"worst gc pause {FrameStats.WorstGcPauseMs:F1} ms, expected 6");
            // 50 - 30 far - 2 opaque - 8 tick = 10 outside the stages, of which 4 was the swap
            if (Math.Abs(FrameStats.WorstOutsideMs - 10) > 0.5)
                throw new Exception($"worst outside {FrameStats.WorstOutsideMs:F1} ms, expected 10");
            if (Math.Abs(FrameStats.WorstSwapMs - 4) > 0.5)
                throw new Exception($"worst swap {FrameStats.WorstSwapMs:F1} ms, expected 4");
            // and the average must NOT look like the spike
            if (FrameStats.StageMs[far] > 5)
                throw new Exception("averages absorbed the spike");

            // the HUD tail ranks the same data: the spike's stage first
            string tail = DebugHud.WorstFrameTail();
            if (tail == null || !tail.StartsWith("schatten 30"))
                throw new Exception($"HUD tail '{tail}' does not lead with the spike");
            if (!tail.Contains("gc 6"))
                throw new Exception($"HUD tail '{tail}' lost the GC pause");
            // the outside bucket splits into swap and the rest - both must carry their share,
            // and neither may double-count the other
            if (!tail.Contains("tick 8"))
                throw new Exception($"HUD tail '{tail}' should rank tick (8) over swap (4) and draussen (6)");
            if (!tail.Contains("draussen 6"))
                throw new Exception($"HUD tail '{tail}' - draussen must be outside minus swap (6), not the whole outside");

            // a later, bigger spike replaces the snapshot even inside the same peak window
            Frame(60, 45, 1, 2, 0);
            if (Math.Abs(FrameStats.WorstStageMs[opaque] - 45) > 0.5)
                throw new Exception("second spike did not replace the snapshot");
            if (Math.Abs(FrameStats.WorstGcPauseMs) > 0.01)
                throw new Exception("old GC pause leaked into the new snapshot");

            FrameStats.Reset();
        });

        Check("hitch log books spikes with camera attribution and yaw wrap", () =>
        {
            // The claim under test: a frame over the threshold is booked with the correct
            // dominant bucket, its GC pause, and the camera's turn/move rate for exactly that
            // frame - including across the 0/2pi yaw seam. Counter-checks that were run once
            // by breaking the code: without the wrap the seam spike reads ~8800 grad/s
            // (asserted ~119); without the 15 ms floor the 13 ms probe frame books. Note the
            // first wrap mutation (dropping only the modulo) changed nothing - the two
            // add/subtract lines wrap any single-revolution delta on their own, so a wrap
            // counter-check must replace the whole function body.
            FrameStats.Reset();
            HitchLog.Reset();
            HitchLog.MinMs = 15;
            HitchLog.Factor = 2.0;

            long msTicks = System.Diagnostics.Stopwatch.Frequency / 1000;
            long clock = 987654321;
            double gcTotal = 50;
            int opaqueStage = (int)Vintagestory.API.Client.EnumRenderStage.Opaque;
            double x = 0;

            // real per-frame order: Advance (detects, holds pending), then the frame boundary
            // samples the camera, which commits the pending hitch with that frame's rates
            void Frame(long lengthMs, long opaqueMs, long tickMs, double gcMs, double yawAfter, double xAfter)
            {
                FrameStats.AddStageTicks(opaqueStage, opaqueMs * msTicks);
                FrameStats.AddGameTickTicks(tickMs * msTicks);
                gcTotal += gcMs;
                clock += lengthMs * msTicks;
                FrameStats.Advance(clock, gcTotal);
                HitchLog.NoteCamera(yawAfter, 0.2, xAfter, 64, 0);
            }

            FrameStats.Advance(clock, gcTotal);
            HitchLog.NoteCamera(1.0, 0.2, x, 64, 0);

            for (int i = 0; i < 60; i++) Frame(6, 4, 2, 0, 1.0, x);
            if (HitchLog.TotalHitches != 0)
                throw new Exception($"quiet phase booked {HitchLog.TotalHitches} hitches");

            // 13 ms is over the factor criterion (2x the ~6 ms average) but under the 15 ms
            // floor - only the floor keeps it out, so this frame isolates exactly that rule
            Frame(13, 10, 2, 0, 1.0, x);
            if (HitchLog.TotalHitches != 0) throw new Exception("floor ignored: 13 ms frame was booked");

            // the real spike: 40 ms, opaque-heavy, 5 ms GC pause, camera turning 0.3 rad in
            // this one frame = ~430 grad/s
            Frame(40, 30, 4, 5, 1.3, x);
            if (HitchLog.TotalHitches != 1) throw new Exception($"spike not booked ({HitchLog.TotalHitches})");
            if (!HitchLog.TryGetLast(out HitchLog.Entry e)) throw new Exception("ring empty after booking");
            if (Math.Abs(e.FrameMs - 40) > 0.5) throw new Exception($"frame {e.FrameMs:F1} ms, expected 40");
            if (HitchLog.DominantBucket(e.Buckets) != HitchLog.Opaque)
                throw new Exception("dominant bucket is not opaque");
            if (Math.Abs(e.Buckets[HitchLog.Opaque] - 30) > 0.5)
                throw new Exception($"opaque bucket {e.Buckets[HitchLog.Opaque]:F1}, expected 30");
            if (Math.Abs(e.GcPauseMs - 5) > 0.1) throw new Exception($"gc {e.GcPauseMs:F1}, expected 5");
            if (double.IsNaN(e.TurnDegPerSec) || Math.Abs(e.TurnDegPerSec - 430) > 15)
                throw new Exception($"turn rate {e.TurnDegPerSec:F0} grad/s, expected ~430");
            if (HitchLog.CountTurning != 1 || HitchLog.CountStill != 0)
                throw new Exception($"turning {HitchLog.CountTurning}, still {HitchLog.CountStill} - expected 1/0");

            // A spike frame with known sweep and upload shares must carry them into the entry:
            // this is the attribution that separates "the mod's sweep spiked" from "the driver
            // queue drained inside a stage" - the question every schatten/opaque hitch report
            // has needed answered first.
            FrameStats.AddCullTicks(7 * msTicks);
            FrameStats.AddUploadMs(3.0);
            Frame(38, 28, 4, 0, 1.3, x);
            if (!HitchLog.TryGetLast(out HitchLog.Entry eAttr)) throw new Exception("attribution spike not booked");
            if (Math.Abs(eAttr.SweepMs - 7) > 0.5)
                throw new Exception($"sweep share {eAttr.SweepMs:F1} ms, expected 7");
            if (Math.Abs(eAttr.UploadMs - 3) > 0.1)
                throw new Exception($"upload share {eAttr.UploadMs:F1} ms, expected 3");
            if (!HitchLog.FormatEntry(in eAttr).Contains("davon sweep"))
                throw new Exception("the formatted line does not carry the sweep attribution");

            // A sweep that was mostly waiting for helper threads has to say so - "the sweep took
            // 30 ms" and "the sweep waited 29 ms for a core" call for opposite fixes, and the
            // ThreadPool version of the batch could not tell them apart at all.
            FrameStats.AddCullTicks(20 * msTicks);
            FrameStats.AddCullWaitTicks(18 * msTicks);
            Frame(45, 35, 4, 0, 1.3, x);
            if (!HitchLog.TryGetLast(out HitchLog.Entry eWait)) throw new Exception("wait spike not booked");
            if (Math.Abs(eWait.SweepWaitMs - 18) > 0.5)
                throw new Exception($"wait share {eWait.SweepWaitMs:F1} ms, expected 18");
            if (!HitchLog.FormatEntry(in eWait).Contains("warten auf threads"))
                throw new Exception("a wait-dominated sweep did not name the wait");
            // ...and a sweep that really did the work must NOT claim a stall
            if (HitchLog.FormatEntry(in eAttr).Contains("warten auf threads"))
                throw new Exception("a sweep with no wait was reported as stalled");

            // movement spike: tick-heavy, one block in 36 ms = ~28 m/s, camera not turning
            Frame(36, 4, 25, 0, 1.3, x + 1.0);
            x += 1.0;
            if (HitchLog.TotalHitches != 4) throw new Exception("movement spike not booked");
            HitchLog.TryGetLast(out e);
            if (HitchLog.DominantBucket(e.Buckets) != HitchLog.Tick)
                throw new Exception("dominant bucket is not tick");
            if (e.MoveBlocksPerSec < HitchLog.MoveThresholdBlocksPerSec)
                throw new Exception($"move rate {e.MoveBlocksPerSec:F1} under threshold");
            if (HitchLog.CountMoving != 1 || HitchLog.CountTurning != 1)
                throw new Exception("movement spike miscounted");
            if (HitchLog.DominantCount(HitchLog.Tick) != 1 || HitchLog.DominantCount(HitchLog.Opaque) != 3)
                throw new Exception("dominant bucket counters wrong");

            // yaw seam: end a quiet frame just below 2pi, then spike while crossing to 0.05 -
            // a ~4.8 grad step that must read as ~119 grad/s, not as a full revolution
            Frame(8, 5, 2, 0, 6.25, x);
            Frame(40, 30, 4, 0, 0.05, x);
            HitchLog.TryGetLast(out e);
            if (Math.Abs(e.TurnDegPerSec - 119) > 25)
                throw new Exception($"seam turn rate {e.TurnDegPerSec:F0} grad/s, expected ~119 - yaw not wrapped?");

            // a hitch whose camera sample never arrives (menu) books as unknown, not as still
            FrameStats.AddStageTicks(opaqueStage, 30 * msTicks);
            clock += 40 * msTicks;
            FrameStats.Advance(clock, gcTotal);
            FrameStats.AddStageTicks(opaqueStage, 5 * msTicks);
            clock += 8 * msTicks;
            FrameStats.Advance(clock, gcTotal); // commits the pending hitch without a camera
            HitchLog.TryGetLast(out e);
            if (!double.IsNaN(e.TurnDegPerSec)) throw new Exception("camera-less hitch got a turn rate");
            // exactly two "still" hitches so far: the sweep-attribution spike and the
            // wait-attribution one, both with a camera present and not moving. The camera-less
            // one must NOT have raised it - unknown is not still.
            if (HitchLog.CountStill != 2) throw new Exception("camera-less hitch counted as still");
            if (HitchLog.TotalHitches != 6) throw new Exception($"expected 6 hitches, got {HitchLog.TotalHitches}");

            // GC generation tagging: the pure rule (highest generation wins, none = null),
            // and end to end through the five-argument Advance seam. Counter-checked once by
            // flipping the precedence to gen0-first: the rule assert fails.
            if (HitchLog.GcGenTag(1, 0, 1) != "gen2") throw new Exception("gen2 must outrank gen0");
            if (HitchLog.GcGenTag(2, 1, 0) != "gen1") throw new Exception("gen1 must outrank gen0");
            if (HitchLog.GcGenTag(3, 0, 0) != "gen0") throw new Exception("plain gen0 not tagged");
            if (HitchLog.GcGenTag(0, 0, 0) != null) throw new Exception("no collection must mean no tag");

            FrameStats.Advance(clock, gcTotal, 100, 10, 5); // establish known counts
            FrameStats.AddStageTicks(opaqueStage, 30 * msTicks);
            gcTotal += 25;
            clock += 40 * msTicks;
            FrameStats.Advance(clock, gcTotal, 103, 11, 6); // a gen2 ran during the spike
            FrameStats.AddStageTicks(opaqueStage, 5 * msTicks);
            clock += 8 * msTicks;
            FrameStats.Advance(clock, gcTotal, 103, 11, 6); // commits the pending hitch
            HitchLog.TryGetLast(out e);
            if (e.GcTag != "gen2") throw new Exception($"spike with a gen2 tagged '{e.GcTag}'");
            if (HitchLog.CountGen2 != 1) throw new Exception($"gen2 counter {HitchLog.CountGen2}, expected 1");
            if (!HitchLog.FormatEntry(in e).Contains("(gen2)"))
                throw new Exception("log line does not carry the generation tag");

            // the report is chat-bound: VTML parses angle brackets, so none may appear
            string report = HitchLog.BuildReport();
            if (!report.Contains("beim drehen") || !report.Contains("opaque"))
                throw new Exception("report is missing its aggregates");
            if (report.Contains("<") || report.Contains(">"))
                throw new Exception("VTML-unsafe angle bracket in the report");

            // the log rate limiter: six lines per window, then suppression, fresh window resets
            for (int i = 0; i < 6; i++)
                if (!HitchLog.RateLimitAllows(1000)) throw new Exception($"line {i + 1} suppressed too early");
            if (HitchLog.RateLimitAllows(1000)) throw new Exception("seventh line not suppressed");
            if (!HitchLog.RateLimitAllows(1031)) throw new Exception("new window did not reset the limiter");

            HitchLog.Reset();
            FrameStats.Reset();
            if (HitchLog.TotalHitches != 0) throw new Exception("reset did not clear the log");
        });

        MethodInfo onBeforeFrame = AccessTools.Method(
            AccessTools.TypeByName("Vintagestory.Client.NoObf.ChunkTesselatorManager"), "OnBeforeFrame");
        Check("upload budget transpiler", () => { UploadBudgetPatches.Apply(harmony); ForceJit(onBeforeFrame); });

        Check("priority uploads are budgeted with liveness, and the remainder carries", () =>
        {
            // the prefix applies on top of the measurement patch and the transpiler
            PrioUploadPatches.Apply(harmony);
            ForceJit(onBeforeFrame);

            // the cap rule: three gain-scaled bases, never below one full chunk mesh
            if (PrioUploadPatches.CapVertices(49494, 65536) != 148482)
                throw new Exception("cap is not three times the scaled base");
            if (PrioUploadPatches.CapVertices(2048, 65536) != 65536)
                throw new Exception("the one-full-chunk floor did not hold at collapsed gain");

            // the continue rule: liveness first, then the cap
            if (!PrioUploadPatches.ShouldContinue(0, 0, 0))
                throw new Exception("liveness: the first entry of a frame must always run");
            if (PrioUploadPatches.ShouldContinue(70000, 1, 65536))
                throw new Exception("the cap was ignored once liveness was satisfied");

            // drain mechanics against a real queue of fake entries
            static Vintagestory.Client.NoObf.TesselatedChunk Fake(int verts)
            {
                var tc = (Vintagestory.Client.NoObf.TesselatedChunk)RuntimeHelpers.GetUninitializedObject(
                    typeof(Vintagestory.Client.NoObf.TesselatedChunk));
                PrioUploadPatches.TcVerts(tc) = verts;
                return tc;
            }
            var q = new Queue<Vintagestory.Client.NoObf.TesselatedChunk>();
            for (int i = 0; i < 10; i++) q.Enqueue(Fake(1000));
            int uploaded = 0;
            int verts = PrioUploadPatches.DrainBudgeted(q, 2500, tc => { uploaded++; return PrioUploadPatches.TcVerts(tc); });
            if (uploaded != 3 || verts != 3000)
                throw new Exception($"cap 2500 drained {uploaded} entries / {verts} verts, expected 3 / 3000");
            if (q.Count != 7) throw new Exception($"remainder {q.Count}, expected 7 still queued");

            // the remainder moves next frame even against a cap it can never fit under
            verts = PrioUploadPatches.DrainBudgeted(q, 1, tc => PrioUploadPatches.TcVerts(tc));
            if (verts != 1000 || q.Count != 6)
                throw new Exception("a tiny cap must still move exactly one entry per frame");

            // entries whose chunk is gone report zero vertices and never block the drain
            var dead = new Queue<Vintagestory.Client.NoObf.TesselatedChunk>();
            for (int i = 0; i < 5; i++) dead.Enqueue(Fake(1000));
            int disposed = 0;
            PrioUploadPatches.DrainBudgeted(dead, 65536, _ => { disposed++; return 0; });
            if (disposed != 5 || dead.Count != 0)
                throw new Exception("dead entries must drain completely - they cost no budget");
        });

        Console.WriteLine("\nbehaviour:");

        Check("ToLowerInvariant really gone from OnFrame", () =>
        {
            int calls = 0;
            foreach (CodeInstruction ins in PatchProcessor.GetCurrentInstructions(onFrame))
                if (ins.operand is MethodInfo mi && mi.Name == "ToLowerInvariant") calls++;
            if (calls != 0) throw new Exception($"{calls} ToLowerInvariant call(s) still present");
        });

        Check("Enumerable.Any really gone from OnClientFrame", () =>
        {
            foreach (CodeInstruction ins in PatchProcessor.GetCurrentInstructions(onClientFrame))
                if (ins.operand is MethodInfo mi && mi.Name == "Any" && mi.DeclaringType == typeof(System.Linq.Enumerable))
                    throw new Exception("Enumerable.Any still present");
        });

        Check("animator activates a mixed-case animation code", () =>
        {
            var anim = new Animation { Code = "WALK", QuantityFrames = 1, KeyFrames = Array.Empty<AnimationKeyFrame>() };
            var animator = new TestAnimator(() => 1.0, new[] { anim });

            // the ctor lowercases codes; the lookup key here is deliberately a different case
            var active = new Dictionary<string, AnimationMetaData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Walk"] = new AnimationMetaData { Code = "Walk", Animation = "Walk" }
            };

            animator.OnFrame(active, 1f / 60f);

            RunningAnimation state = animator.GetAnimationState("wAlK");
            if (state == null) throw new Exception("GetAnimationState returned null for a mixed-case code");
            if (!state.Active) throw new Exception("animation did not become active");
            if (active.Count != 1) throw new Exception("animator dropped the animation as unknown");
        });

        Check("AdjustCollisionBox helper matches LINQ", () =>
        {
            var empty = new Dictionary<string, AnimationMetaData>();
            var noBox = new Dictionary<string, AnimationMetaData> { ["a"] = new AnimationMetaData() };
            var withBox = new Dictionary<string, AnimationMetaData> { ["a"] = new AnimationMetaData(), ["b"] = new AnimationMetaData { AdjustCollisionBox = true } };
            foreach (var d in new[] { empty, noBox, withBox })
            {
                bool linq = System.Linq.Enumerable.Any(d, kv => kv.Value.AdjustCollisionBox);
                if (AnimationPatches.AnyAdjustCollisionBox(d) != linq) throw new Exception("mismatch");
            }
        });

        Check("upload budget call landed in the right expression", () =>
        {
            // expected shape: ldfld ViewDistanceSq | ldc 48 | div | ldc 350 | add | call Scale | stloc
            var code = new List<CodeInstruction>(PatchProcessor.GetCurrentInstructions(onBeforeFrame));
            FieldInfo vds = AccessTools.Field(typeof(Vintagestory.API.Client.FrustumCulling),
                                              nameof(Vintagestory.API.Client.FrustumCulling.ViewDistanceSq));
            int at = code.FindIndex(c => c.opcode == OpCodes.Ldfld && ReferenceEquals(c.operand, vds));
            if (at < 0) throw new Exception("anchor gone");

            int call = -1;
            for (int i = at; i < Math.Min(code.Count, at + 10); i++)
                if (code[i].opcode == OpCodes.Call && code[i].operand is MethodInfo mi
                    && mi.DeclaringType == typeof(UploadBudget) && mi.Name == nameof(UploadBudget.Scale)) { call = i; break; }
            if (call < 0) throw new Exception("Scale call not inserted near the anchor");

            if (code[call - 1].opcode != OpCodes.Add) throw new Exception($"expected 'add' before the call, got {code[call - 1].opcode}");
            OpCode after = code[call + 1].opcode;
            bool isStore = after == OpCodes.Stloc || after == OpCodes.Stloc_S || after == OpCodes.Stloc_0
                        || after == OpCodes.Stloc_1 || after == OpCodes.Stloc_2 || after == OpCodes.Stloc_3;
            if (!isStore) throw new Exception($"expected a local store after the call, got {after}");
        });

        Check("frame stats publish within a second, not after a window", () =>
        {
            FrameStats.Reset();
            long tenth = System.Diagnostics.Stopwatch.Frequency / 10000; // 0.1 ms

            // the HUD must come alive quickly - this is what was broken in 1.3.1
            for (int i = 0; i < 20; i++)
            {
                FrameStats.BeginFrame();
                FrameStats.AddCullTicks(tenth);
                FrameStats.AddUploadMs(0.5);
                System.Threading.Thread.Sleep(1);
            }
            FrameStats.BeginFrame();
            if (!FrameStats.HasData) throw new Exception($"still no data after {FrameStats.TotalFrames} frames");

            // and it must converge on the right value
            for (int i = 0; i < 400; i++)
            {
                FrameStats.BeginFrame();
                FrameStats.AddCullTicks(tenth);
                System.Threading.Thread.Sleep(1);
            }
            FrameStats.BeginFrame();
            if (FrameStats.AvgFrameMs <= 0) throw new Exception("frame time not measured");
            if (FrameStats.AvgCullMs < 0.05 || FrameStats.AvgCullMs > 0.5)
                throw new Exception($"cull time implausible: {FrameStats.AvgCullMs:F3} ms (expected ~0.1)");
            FrameStats.Reset();
        });

        Check("upload throttle never exceeds vanilla's budget", () =>
        {
            UploadBudget.Reset();
            UploadBudget.Enabled = true;
            UploadBudget.TargetMs = 6.0;
            if (UploadBudget.Scale(100000) != 100000) throw new Exception("gain should start at 1.0");

            // a run of frames that all overshoot must throttle, and never below the floor
            for (int i = 0; i < 40; i++) { UploadBudget.FrameStart(); System.Threading.Thread.Sleep(8); UploadBudget.FrameEnd(); }
            int throttled = UploadBudget.Scale(100000);
            if (throttled >= 100000) throw new Exception($"did not throttle ({throttled})");
            if (throttled < 2048) throw new Exception($"throttled below the floor ({throttled})");

            // and cheap frames must let it climb back, but not past 1.0
            for (int i = 0; i < 80; i++) { UploadBudget.FrameStart(); UploadBudget.FrameEnd(); }
            if (UploadBudget.Scale(100000) > 100000) throw new Exception("gain rose above 1.0");
            UploadBudget.Reset();
        });

        Check("HUD text composes without a GL context", () =>
        {
            FrameStats.Reset();
            string cold = DebugHud.Compose("test", 0, 1536, 0, 0, 0, 0, null);
            if (string.IsNullOrWhiteSpace(cold)) throw new Exception("empty text before any data");

            long t = System.Diagnostics.Stopwatch.Frequency / 1000;
            for (int i = 0; i < 250; i++)
            {
                FrameStats.BeginFrame();
                FrameStats.AddStageTicks((int)Vintagestory.API.Client.EnumRenderStage.Opaque, t * 4);
                FrameStats.AddCullTicks(t * 2);
                System.Threading.Thread.Sleep(1);
            }
            FrameStats.BeginFrame();

            string text = DebugHud.Compose("test", 1234, 1536, 64 * 1048576, 120, 0.07f, 41024, null);
            string[] lines = text.Split('\n');
            if (lines.Length < 20) throw new Exception($"only {lines.Length} lines");
            foreach (string needle in new[] { "fps", "game tick", "opaque", "schatten", "draw calls", "terrain vram", "sichtweite" })
                if (!text.Contains(needle)) throw new Exception($"missing '{needle}'");
            // every line must be non-empty - blank lines were the original complaint
            foreach (string line in lines)
                if (line.Trim().Length == 0) throw new Exception("blank line in HUD text");

            // The share bars are pure geometry the raster relies on: ten cells represent the
            // whole frame, nothing may exceed them, and anything visible gets a sliver.
            if (DebugHud.Bar(5, 10) != "█████") throw new Exception($"half a frame must be five cells, got '{DebugHud.Bar(5, 10)}'");
            if (DebugHud.Bar(0, 10) != "") throw new Exception("an empty bucket drew a bar");
            if (DebugHud.Bar(25, 10).Length > 10) throw new Exception("a bucket longer than the frame must clamp at ten cells");
            if (DebugHud.Bar(0.06, 10).Length != 1) throw new Exception("a visible bucket lost its sliver");
            if (DebugHud.Bar(3, 10).Length < DebugHud.Bar(2, 10).Length) throw new Exception("bars not monotonic");
            bool savedAscii = DebugHud.BarAscii;
            try
            {
                // the fallback for fonts whose block glyphs are not one monospace cell wide
                DebugHud.BarAscii = true;
                if (DebugHud.Bar(5, 10) != "#####") throw new Exception("ascii fallback broken");
            }
            finally { DebugHud.BarAscii = savedAscii; }
            FrameStats.Reset();
        });

        Check("a long ephemeral GC pause under server-gc is called out, a short one is not", () =>
        {
            // The rule that reverses my own earlier advice. Server GC was recommended here on
            // throughput grounds; a 65 ms stop-the-world *gen0* pause is what that costs on a
            // six core desktop, and no amount of concurrency helps because ephemeral collections
            // are never background.
            if (HitchLog.GcModeVerdict(true, 65) == null)
                throw new Exception("a 65 ms gen0 freeze under server-gc went unmentioned");
            if (!HitchLog.GcModeVerdict(true, 65).Contains("DOTNET_gcServer=0"))
                throw new Exception("the verdict does not say what to actually change");
            // ordinary pauses say nothing about the mode and must not produce advice
            if (HitchLog.GcModeVerdict(true, 6) != null)
                throw new Exception("a 6 ms pause triggered a mode change recommendation");
            // and workstation is never told to switch away from itself
            if (HitchLog.GcModeVerdict(false, 200) != null)
                throw new Exception("workstation gc was told to leave server gc");
        });

        Check("the cull threads run every slice exactly once, batch after batch", () =>
        {
            // The primitive underneath the parallel sweep. A dropped slice is invisible
            // geometry, a doubled one is a doubled draw range - neither shows up as a crash,
            // and both would be blamed on the culling maths for weeks.
            var set = new WorkerSet("verify-workers");
            set.Start(4);
            try
            {
                if (set.ThreadCount != 4) throw new Exception($"{set.ThreadCount} threads, wanted 4");

                foreach (int n in new[] { 1, 7, 64, 1000, 4097 })
                    foreach (int chunk in new[] { 1, 3, 64 })
                    {
                        var hits = new int[n];
                        var body = new CountingBody { Hits = hits };
                        set.Run(body, n, chunk);
                        for (int i = 0; i < n; i++)
                            if (hits[i] != 1)
                                throw new Exception($"n={n} chunk={chunk}: index {i} ran {hits[i]} times");
                    }

                // Repeated batches must not leak state from the previous one: the gate/pending
                // handshake is the part that would deadlock or fire early if it did.
                for (int round = 0; round < 200; round++)
                {
                    var hits = new int[257];
                    set.Run(new CountingBody { Hits = hits }, 257, 8);
                    for (int i = 0; i < 257; i++)
                        if (hits[i] != 1) throw new Exception($"round {round}: index {i} ran {hits[i]} times");
                }

                // A throwing work item has to surface on the caller and, above all, must not
                // leave the caller waiting on a countdown that never reaches zero.
                bool threw = false;
                try { set.Run(new ThrowingBody(), 400, 8); }
                catch (InvalidOperationException) { threw = true; }
                if (!threw) throw new Exception("a failing work item was swallowed");

                // ...and the set still works afterwards
                var after = new int[100];
                set.Run(new CountingBody { Hits = after }, 100, 8);
                for (int i = 0; i < 100; i++)
                    if (after[i] != 1) throw new Exception("the set did not recover from a failed batch");
            }
            finally { set.Stop(); }
        });

        Check("deprioritised workers really are deprioritised by the OS, not just asked to be", () =>
        {
            // Thread.Priority = BelowNormal is accepted on Linux, reads back as BelowNormal, and
            // leaves the thread at the process nice value. Measured before this was written: the
            // worker sat at nice -4, exactly like the main thread. So the claim is checked
            // against /proc, which is the only place that knows.
            if (!OperatingSystem.IsLinux()) return;

            var set = new WorkerSet("verify-nice", niceness: 5);
            set.Start(2);
            try
            {
                var seen = new int[64];
                // one batch, so every worker has run its body and therefore its start-up
                set.Run(new CountingBody { Hits = seen }, 64, 4);
                if (!set.PriorityLowered)
                    throw new Exception("setpriority did not report success");

                // /proc/self/stat is the thread group leader - the main thread - which is the
                // baseline the workers have to be below. CurrentManagedThreadId is a managed id
                // and has nothing to do with the OS tid the task directories are named after.
                int mainNice = NiceOf("/proc/self");
                int lowered = 0, total = 0;
                foreach (string dir in System.IO.Directory.GetDirectories("/proc/self/task"))
                {
                    if (System.IO.File.ReadAllText(dir + "/comm").Trim() is not "verify-nice-0" and not "verify-nice-1")
                        continue;
                    total++;
                    if (NiceOf(dir) > mainNice) lowered++;
                }
                if (total == 0) throw new Exception("the worker threads were not found in /proc");
                if (lowered != total)
                    throw new Exception($"{total - lowered} of {total} workers kept the main thread's priority");
            }
            finally { set.Stop(); }
        });

        Check("with no threads started the work still runs, inline", () =>
        {
            var set = new WorkerSet("verify-empty");
            var hits = new int[500];
            set.Run(new CountingBody { Hits = hits }, 500, 8);
            for (int i = 0; i < 500; i++)
                if (hits[i] != 1) throw new Exception($"index {i} ran {hits[i]} times inline");
        });

        Check("patched MeshDataPool.FrustumCull routes through FastCuller", () =>
        {
            long before = FastCuller.StatSweeps;
            MeshDataPool pool = NewPool();
            pool.FrustumCull(NewCuller(), EnumFrustumCullMode.CullNormal);
            if (FastCuller.StatSweeps <= before) throw new Exception("prefix did not run");
        });

        Check("a stage switch re-culls the pools seen before the batch fires", () =>
        {
            // The regression this guards: pools culled early in a stage kept the previous
            // stage's visibility, because the batch stamp from that stage was still current.
            // Shadow pass results leaking into the opaque pass look like holes in the world.
            FastCuller.Parallel = true;
            FrustumCulling culler = NewCuller();

            var pools = new List<MeshDataPool>();
            for (int i = 0; i < 12; i++) pools.Add(NewPool());

            // stage one: normal culling, enough pools that the parallel batch fires
            foreach (MeshDataPool p in pools) FastCuller.Cull(p, culler, EnumFrustumCullMode.CullNormal);

            // stage two: a different mode. The very first pool must be re-culled, not reused.
            MeshDataPool first = pools[0];
            FastCuller.Cull(first, culler, EnumFrustumCullMode.CullInstantShadowPassFar);
            int got = first.indicesGroupsCount;

            // what vanilla produces for that mode on the same pool
            FastCuller.Parallel = false;
            MeshDataPool reference = NewPool();
            reference.FrustumCull(culler, EnumFrustumCullMode.CullInstantShadowPassFar);
            int want = reference.indicesGroupsCount;
            FastCuller.Parallel = true;

            if (got != want)
                throw new Exception($"stale batch result: {got} draw ranges, vanilla says {want}");
        });

        Check("a part inserted mid-list is culled on its FINAL LodLevel and CullVisible", () =>
        {
            // The field bug, reproduced exactly. TesselatedChunkPart does:
            //
            //     ModelDataPoolLocation loc = pools.AddModel(...);  // our TryAdd postfix here
            //     loc.CullVisible = cullVisible;                    // only NOW
            //     loc.LodLevel = lodLevel;                          // only NOW
            //
            // so a postfix that snapshots those two reads LodLevel 0 and a throwaway Bools.
            // LodLevel 0 means "invisible unless the LOD0 setting is on", so every part
            // TrySqueezeInbetween squeezed into a fragmented pool vanished from the camera pass
            // until a rebuild brought it back - visible in game as flickering geometry. Every
            // existing test missed it because they all built their locations fully populated.
            FastCuller.ForgetAllPools();
            FastCuller.Parallel = false;
            FrustumCulling culler = NewCuller();
            // The field condition, and the whole reason a wrong LodLevel is fatal rather than
            // merely inaccurate: without the LOD0 setting lod0BiasSq is 0, and vanilla's
            // switch then returns false for level 0 at ANY distance. A test culler with
            // lod0BiasSq set draws LOD 0 parts near the camera and survives the bug.
            culler.lod0BiasSq = 0f;

            MeshDataPool pool = NewPool();
            var locations = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(pool);

            // build the cache first, so NoteInserted takes the incremental path
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);

            // a part in the middle, in the state TryAdd leaves it: LodLevel 0, default Bools.
            // Its geometry is copied from a neighbour that the culler definitely draws.
            var inserted = new ModelDataPoolLocation
            {
                IndicesStart = 40 * 300 + 30,
                IndicesEnd = 40 * 300 + 90,
                FrustumCullSphere = locations[0].FrustumCullSphere,
            };
            locations.Insert(40, inserted);
            FastCuller.NoteInserted(pool, inserted);

            // ...and only afterwards does the engine fill in the two fields
            inserted.LodLevel = 1;
            inserted.CullVisible = locations[0].CullVisible;

            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);

            // The verifier's own rule is what decides, so this test and the in-game check
            // cannot drift apart on what "the same" means.
            var want = new List<int>();
            foreach (ModelDataPoolLocation loc in locations)
            {
                if (loc.Hide || !loc.CullVisible[ModelDataPoolLocation.VisibleBufIndex]) continue;
                if (!culler.InFrustumAndRange(loc.FrustumCullSphere, loc.FrustumVisible, loc.LodLevel)) continue;
                want.Add(loc.IndicesStart * 4);
                want.Add(loc.IndicesEnd - loc.IndicesStart);
            }
            if (want.Count == 0) throw new Exception("vanilla drew nothing - the test proves nothing");

            string problem = CullVerifier.Compare(pool.indicesStartsByte, pool.indicesSizes,
                                                  pool.indicesGroupsCount, want);
            if (problem != null)
                throw new Exception("inserted part not culled like vanilla: " + problem);

            // And the same for occlusion: a stale Bools snapshot is permanently (true, true),
            // so hiding the part through the shared object has to actually hide it.
            inserted.CullVisible[ModelDataPoolLocation.VisibleBufIndex] = false;
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
            if (Covers(Drawn(pool), inserted.IndicesStart * 4,
                       (inserted.IndicesEnd - inserted.IndicesStart) * 4))
                throw new Exception("occluded part was still drawn - CullVisible is being cached");
            inserted.CullVisible[ModelDataPoolLocation.VisibleBufIndex] = true;

            FastCuller.ForgetAllPools();
        });

        Check("the sweep checker accepts merged ranges and nothing else", () =>
        {
            // Merging is legal only across parts that are genuinely back to back in the index
            // buffer; a checker that allows more would have waved the LodLevel bug through.
            var want = new List<int> { 0, 30, 120, 30, 400, 12 };   // bytes 0..120, 120..240, 400..448

            int[] starts = { 0, 0, 400, 0 };
            int[] sizes = { 60, 12 };
            if (CullVerifier.Compare(starts, sizes, 2, want) != null)
                throw new Exception("a legal merge of two adjacent parts was rejected: "
                                    + CullVerifier.Compare(starts, sizes, 2, want));

            // one range spanning the gap between 240 and 400 must not pass
            int[] overreach = { 0, 0 };
            int[] overSizes = { 102 };
            if (CullVerifier.Compare(overreach, overSizes, 1, want) == null)
                throw new Exception("a range spanning a gap was accepted");

            // a part vanilla draws that was never emitted must be reported
            int[] shortStarts = { 0, 0 };
            int[] shortSizes = { 60 };
            if (CullVerifier.Compare(shortStarts, shortSizes, 1, want) == null)
                throw new Exception("a missing part was accepted");

            // and one emitted that vanilla does not draw
            int[] extra = { 0, 0, 400, 0, 900, 0 };
            int[] extraSizes = { 60, 12, 9 };
            if (CullVerifier.Compare(extra, extraSizes, 3, want) == null)
                throw new Exception("an extra range was accepted");
        });

        Check("the sweep checker accepts a bridged gap only when every byte is attributed", () =>
        {
            // Vanilla draws bytes 0..120 and 400..448; the part at 240..400 is invisible but
            // provably outside the frustum, so a range bridged across it is pixel-identical.
            var want = new List<int> { 0, 30, 400, 12 };
            var allowed = new List<int> { 240, 40 };            // bytes 240..400

            // bytes 120..240 belong to no part at all - a bridge across free space stays illegal
            int[] starts = { 0, 0 };
            int[] sizes = { 112 };                              // 0..448 in one range
            if (CullVerifier.Compare(starts, sizes, 1, want, allowed) == null)
                throw new Exception("a bridge across unattributed bytes was accepted");

            // with the missing stretch attributed too, the same range is legal
            allowed.Add(120); allowed.Add(30);                  // bytes 120..240
            string problem = CullVerifier.Compare(starts, sizes, 1, want, allowed);
            if (problem != null)
                throw new Exception("a fully attributed bridge was rejected: " + problem);

            // a range must still END on a part vanilla draws - trailing filler means the sweep
            // widened a range past the last visible part
            int[] longSizes = { 122 };                          // 0..488, 40 bytes past the end
            allowed.Add(448); allowed.Add(10);
            if (CullVerifier.Compare(starts, longSizes, 1, want, allowed) == null)
                throw new Exception("a range ending on bridged filler was accepted");
        });

        Check("gap merging bridges frustum-clipped parts and refuses everything else", () =>
        {
            // Three parts, contiguous in the index buffer: visible / gap / visible. The gap
            // part cycles through the reasons it can be invisible; only "fully outside the
            // frustum" may be bridged - everything else must keep splitting the range.
            FastCuller.ForgetAllPools();
            FastCuller.Parallel = false;
            bool savedGap = FastCuller.GapMergeDrawRanges;
            FrustumCulling culler = NewCuller();   // eye (0,140,0), looking toward +x

            MeshDataPool BridgePool(ModelDataPoolLocation gapPart)
            {
                ConstructorInfo ctor = typeof(MeshDataPool).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(int), typeof(int), typeof(int) }, null);
                var p = (MeshDataPool)ctor.Invoke(new object[] { 500000, 750000, 16 });
                p.indicesStartsByte = new int[32];
                p.indicesSizes = new int[16];
                var locs = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(p);
                locs.Add(new ModelDataPoolLocation
                {
                    IndicesStart = 0, IndicesEnd = 300, LodLevel = 1,
                    FrustumCullSphere = Sphere.BoundingSphereForCube(100, 130, 0, 8)
                });
                if (gapPart != null) locs.Add(gapPart);
                locs.Add(new ModelDataPoolLocation
                {
                    IndicesStart = 600, IndicesEnd = 900, LodLevel = 1,
                    FrustumCullSphere = Sphere.BoundingSphereForCube(110, 130, 0, 8)
                });
                return p;
            }

            int RangesWith(ModelDataPoolLocation gapPart, EnumFrustumCullMode mode)
            {
                MeshDataPool p = BridgePool(gapPart);
                FastCuller.Cull(p, culler, mode);
                int groups = p.indicesGroupsCount;
                FastCuller.ForgetAllPools();
                return groups;
            }

            ModelDataPoolLocation GapAt(float x, float y, float z, int lod = 1, bool hide = false)
                => new ModelDataPoolLocation
                {
                    IndicesStart = 300, IndicesEnd = 600, LodLevel = lod, Hide = hide,
                    FrustumCullSphere = Sphere.BoundingSphereForCube(x, y, z, 8)
                };

            FastCuller.GapMergeDrawRanges = true;
            try
            {
                long bridgedBefore = FastCuller.StatRangesBridged;

                // behind the camera: provably outside the frustum -> one bridged range
                if (RangesWith(GapAt(-100, 130, 0), EnumFrustumCullMode.CullNormal) != 1)
                    throw new Exception("a frustum-clipped gap part was not bridged");
                if (FastCuller.StatRangesBridged != bridgedBefore + 1)
                    throw new Exception("bridge statistic did not move");

                // same spot, bridging off: the gap must split the range again
                FastCuller.GapMergeDrawRanges = false;
                if (RangesWith(GapAt(-100, 130, 0), EnumFrustumCullMode.CullNormal) != 2)
                    throw new Exception("bridging off still merged across the gap");
                FastCuller.GapMergeDrawRanges = true;

                // in frustum but LOD-rejected (level 3 renders only beyond lod2Bias): LOD 2 and
                // LOD 3 are the same chunk twice, bridging would z-fight
                if (RangesWith(GapAt(105, 130, 0, lod: 3), EnumFrustumCullMode.CullNormal) != 2)
                    throw new Exception("a LOD-rejected in-frustum part was bridged");

                // in frustum but hidden
                if (RangesWith(GapAt(105, 130, 0, hide: true), EnumFrustumCullMode.CullNormal) != 2)
                    throw new Exception("a hidden in-frustum part was bridged");

                // free bytes: no part owns 300..600, the chain cannot close
                if (RangesWith(null, EnumFrustumCullMode.CullNormal) != 2)
                    throw new Exception("a gap of unallocated bytes was bridged");

                // shadow pass: in frustum but outside the shadow range - drawing it would cast
                // a shadow vanilla suppresses
                if (RangesWith(GapAt(300, 130, 0), EnumFrustumCullMode.CullInstantShadowPassNear) != 2)
                    throw new Exception("a range-rejected in-frustum shadow part was bridged");

                // and outside the shadow frustum as well: bridgeable there too
                if (RangesWith(GapAt(-100, 130, 0), EnumFrustumCullMode.CullInstantShadowPassNear) != 1)
                    throw new Exception("a frustum-clipped shadow part was not bridged");
            }
            finally
            {
                FastCuller.GapMergeDrawRanges = savedGap;
                FastCuller.ForgetAllPools();
            }
        });

        Check("the mesh buffer pool reuses, evicts oldest-first and takes over vanilla's stock", () =>
        {
            // The whole test runs against its own recycler instance through the REAL patched
            // engine entry points (GetOrCreateMesh / Dispose->Recycle / DoRecycling), so it
            // exercises the exact call chain the tesselation thread uses.
            var recycler = new MeshDataRecycler(null);
            MeshDataRecycler savedRecycler = MeshData.Recycler;
            Func<long> savedClock = MeshRecyclerPatches.Clock;
            int savedBudget = MeshRecyclerPatches.BudgetMb;
            long now = 1_000_000;
            try
            {
                MeshData.Recycler = recycler;
                MeshRecyclerPatches.Clock = () => now;
                MeshRecyclerPatches.Clear();
                MeshRecyclerPatches.ResetStats();
                MeshRecyclerPatches.BudgetMb = 384;
                MeshRecyclerPatches.Enabled = true;

                // the engine contract: capacity covers the request, the index buffer is 6/4
                // of it, the mesh is marked recyclable, and the basic arrays exist
                MeshData a = recycler.GetOrCreateMesh(1001);
                if (a.VerticesMax < 1004) throw new Exception($"capacity {a.VerticesMax} under the request");
                if (a.IndicesMax != a.VerticesMax * 6 / 4) throw new Exception("index invariant broken");
                if (!a.Recyclable) throw new Exception("mesh not marked recyclable");
                if (a.xyz == null || a.Uv == null || a.Rgba == null || a.Flags == null || a.Indices == null)
                    throw new Exception("basic arrays missing");

                // two outstanding gets must never share a buffer
                MeshData b = recycler.GetOrCreateMesh(1001);
                if (ReferenceEquals(a, b)) throw new Exception("one buffer handed out twice");

                // reuse round trip, with the recycle arriving from another thread like the
                // main thread's AddToPools disposal does
                System.Threading.Tasks.Task.Run(a.Dispose).Wait();
                recycler.DoRecycling();
                if (!ReferenceEquals(recycler.GetOrCreateMesh(1001), a))
                    throw new Exception("returned buffer was not reused");
                if (MeshRecyclerPatches.StatHits != 1) throw new Exception("hit not booked");

                // idle buffers die by TTL
                a.Dispose();
                b.Dispose();
                recycler.DoRecycling();
                now += MeshRecyclerPatches.TtlMs + 1000;
                recycler.DoRecycling();
                if (MeshRecyclerPatches.HeldBytes != 0)
                    throw new Exception($"TTL left {MeshRecyclerPatches.HeldBytes} bytes behind");

                // the byte budget evicts the OLDEST entry, never the youngest - in different
                // size classes on purpose: within one class the front is the oldest by
                // construction, so only a cross-class pair can catch a wrong global pick
                MeshRecyclerPatches.BudgetMb = 1;
                MeshData old = recycler.GetOrCreateMesh(20000);   // ~0,7 MB class
                MeshData young = recycler.GetOrCreateMesh(10000); // ~0,4 MB class
                old.Dispose();
                recycler.DoRecycling();
                now += 600;
                young.Dispose();
                recycler.DoRecycling(); // ~1,1 MB held vs 1 MB budget -> the oldest must go
                if (!ReferenceEquals(recycler.GetOrCreateMesh(10000), young))
                    throw new Exception("budget eviction did not keep the youngest buffer");
                long missesBefore = MeshRecyclerPatches.StatMisses;
                MeshData refetch = recycler.GetOrCreateMesh(20000);
                if (ReferenceEquals(refetch, old) || MeshRecyclerPatches.StatMisses != missesBefore + 1)
                    throw new Exception("budget eviction did not discard the oldest buffer");
                MeshRecyclerPatches.BudgetMb = 384;
                now += MeshRecyclerPatches.TtlMs + 1000;
                recycler.DoRecycling(); // drain and expire everything before the next section

                // enabling takes over vanilla's stored buffers instead of stranding them (our
                // DoRecycling prefix stops vanilla's own TTL sweep from ever running again),
                // and a foreign buffer's index invariant is repaired on hand-out
                MeshRecyclerPatches.SetEnabled(false);
                var vintage = new MeshData(4096);
                vintage.Indices = new int[100];
                vintage.IndicesMax = 100;
                var mediumRef = AccessTools.FieldRefAccess<MeshDataRecycler, SortedList<float, MeshData>>("mediumSizes");
                mediumRef(recycler).Add(4096 / 4, vintage);
                MeshRecyclerPatches.SetEnabled(true);
                recycler.DoRecycling(); // the tesselation-thread moment the takeover runs in
                if (mediumRef(recycler).Count != 0) throw new Exception("vanilla's list was not drained");
                MeshData taken = recycler.GetOrCreateMesh(3600);
                if (!ReferenceEquals(taken, vintage)) throw new Exception("vanilla's buffer does not serve");
                if (taken.IndicesMax != taken.VerticesMax * 6 / 4)
                    throw new Exception("foreign index buffer not repaired");
                taken.Dispose();
                now += MeshRecyclerPatches.TtlMs + 1000;
                recycler.DoRecycling();

                // the point of the whole patch: a steady-state get/dispose cycle allocates
                // (nearly) nothing, where the vanilla storage would allocate the mesh anew
                // whenever its single size slot is taken or the fit is off by >25 %
                MeshData warm = recycler.GetOrCreateMesh(50000);
                warm.Dispose();
                recycler.DoRecycling();
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++)
                {
                    MeshData m = recycler.GetOrCreateMesh(50000);
                    m.Dispose();
                    recycler.DoRecycling();
                }
                long grew = GC.GetAllocatedBytesForCurrentThread() - before;
                if (grew > 200_000)
                    throw new Exception($"steady-state cycle allocated {grew} bytes - the pool is not reusing");

                // gate closed = untouched vanilla path, and our counters stay quiet
                MeshRecyclerPatches.SetEnabled(false);
                long hits = MeshRecyclerPatches.StatHits;
                long misses = MeshRecyclerPatches.StatMisses;
                MeshData vanilla = recycler.GetOrCreateMesh(1000);
                if (vanilla == null || !vanilla.Recyclable) throw new Exception("vanilla fallback broken");
                if (MeshRecyclerPatches.StatHits != hits || MeshRecyclerPatches.StatMisses != misses)
                    throw new Exception("disabled pool still booked stats");
            }
            finally
            {
                MeshRecyclerPatches.Enabled = false;
                MeshRecyclerPatches.Clear();
                MeshRecyclerPatches.ResetStats();
                MeshRecyclerPatches.Clock = savedClock;
                MeshRecyclerPatches.BudgetMb = savedBudget;
                MeshData.Recycler = savedRecycler;
            }
        });

        Check("the forced framebuffer rebuild refuses a window that would null the buffers", () =>
        {
            // SetupDefaultFrameBuffers computes (int)(clientSize * ssaa) per axis and returns
            // its list with every entry still NULL when either is 0; RebuildFrameBuffers then
            // adopts that list and disposes the good buffers, and the next frame dies in
            // ClearFrameBuffer(LiquidDepth) - a shipped tester crash (fullscreen alt-tab
            // during world load = minimised window = ClientSize 0). The guard must refuse
            // exactly the engine's degenerate cases and nothing else.
            if (ShadowResPatches.CanHostFramebuffers(minimized: true, 1920, 1080, 1f))
                throw new Exception("a minimised window was accepted");
            if (ShadowResPatches.CanHostFramebuffers(false, 0, 1080, 1f))
                throw new Exception("zero width was accepted");
            if (ShadowResPatches.CanHostFramebuffers(false, 1920, 0, 1f))
                throw new Exception("zero height was accepted");
            if (ShadowResPatches.CanHostFramebuffers(false, 1, 1, 0.5f))
                throw new Exception("a window the SSAA truncation makes zero-sized was accepted");
            if (!ShadowResPatches.CanHostFramebuffers(false, 2, 2, 0.5f))
                throw new Exception("a viable window was refused");
            if (!ShadowResPatches.CanHostFramebuffers(false, 1920, 1080, 1f))
                throw new Exception("a normal window was refused");
        });

        Check("the HUD throttles its own rebuild and a hitch names the hud share", () =>
        {
            // The interval rule: the overlay spends at most ~4 % of wall time on itself.
            // A Windows tester's i7-4770 measured ~40 ms per Cairo text rebuild; at the old
            // fixed 4 Hz that alone was ~3 booked 40-ms ortho hitches per second.
            if (DebugHud.NextIntervalSeconds(1) != 0.25)
                throw new Exception("cheap rebuilds lost the 4 Hz floor");
            if (Math.Abs(DebugHud.NextIntervalSeconds(40) - 1.0) > 1e-9)
                throw new Exception("a 40 ms rebuild must land at a 1 s cadence");
            if (DebugHud.NextIntervalSeconds(1000) != 2.0)
                throw new Exception("the readability cap is missing");
            if (DebugHud.NextIntervalSeconds(40) <= DebugHud.NextIntervalSeconds(20))
                throw new Exception("interval not monotonic in cost");

            // and the hitch line carries the share, so a HUD-made spike can never again
            // read as an engine ortho problem
            double savedMin = HitchLog.MinMs;
            double savedFactor = HitchLog.Factor;
            try
            {
                HitchLog.MinMs = 15;
                HitchLog.Factor = 2.0;
                HitchLog.Reset();
                var buckets = new double[HitchLog.BucketCount];
                buckets[HitchLog.Ortho] = 41;
                HitchLog.OnFrame(50, 10, 0, buckets, null, 0.5, 0, 0, 38.2);
                HitchLog.OnFrame(10, 10, 0, new double[HitchLog.BucketCount]); // commits it
                if (!HitchLog.BuildReport().Contains(", hud 38"))
                    throw new Exception("hud share missing from the hitch line");

                // a sub-millisecond share stays out - the line only names what explains a spike
                HitchLog.Reset();
                buckets[HitchLog.Ortho] = 41;
                HitchLog.OnFrame(50, 10, 0, buckets, null, 0, 0, 0, 0.4);
                HitchLog.OnFrame(10, 10, 0, new double[HitchLog.BucketCount]);
                if (HitchLog.BuildReport().Contains(", hud "))
                    throw new Exception("a sub-ms hud share was printed");
            }
            finally
            {
                HitchLog.Reset();
                HitchLog.MinMs = savedMin;
                HitchLog.Factor = savedFactor;
            }
        });

        Check("custom-part clones copy the content, not the accumulation capacity", () =>
        {
            TightClonePatches.Apply(harmony);
            ForceJit(AccessTools.Method(typeof(MeshData), "Clone"));

            // the accumulation-buffer shape that cost 217 MB/s in the field: big capacity,
            // little content - and a CustomInts nobody ever wrote to (every non-liquid pass)
            var src = new MeshData(initialiseArrays: false);
            src.SetVerticesCount(4);
            src.xyz = new float[12];
            src.Uv = new float[8];
            src.Rgba = new byte[16];
            src.Flags = new int[4];
            src.Indices = new int[6];
            src.SetIndicesCount(6);
            src.XyzFaces = new byte[500];
            src.XyzFaces[0] = 42;
            src.XyzFacesCount = 3;
            src.CustomFloats = new CustomMeshDataPartFloat(1000)
            {
                InterleaveSizes = new[] { 2 },
                InterleaveOffsets = new[] { 0 },
                InterleaveStride = 8
            };
            for (int i = 0; i < 10; i++) src.CustomFloats.Add(1.5f + i);
            src.CustomFloats.SetAllocationSize(7);
            src.CustomInts = new CustomMeshDataPartInt(2048);

            TightClonePatches.Enabled = true;
            MeshData clone = src.Clone();
            if (clone.CustomFloats.Values.Length != 10)
                throw new Exception($"floats capacity-cloned: {clone.CustomFloats.Values.Length} statt 10");
            if (clone.CustomFloats.Count != 10 || Math.Abs(clone.CustomFloats.Values[9] - 10.5f) > 1e-6)
                throw new Exception("float content lost in the tight copy");
            if (clone.CustomFloats.AllocationSize != 7)
                throw new Exception("the custom GPU allocation size was not carried over");
            if (clone.CustomFloats.InterleaveStride != 8 || clone.CustomFloats.InterleaveSizes[0] != 2)
                throw new Exception("interleave layout lost");
            if (clone.CustomInts.Values.Length != 0 || clone.CustomInts.Count != 0)
                throw new Exception($"the zero-content ints still copy capacity: {clone.CustomInts.Values.Length}");
            if (clone.XyzFaces.Length != 3 || clone.XyzFaces[0] != 42 || clone.XyzFacesCount != 3)
                throw new Exception("per-face extras broken");

            // toggle off = vanilla byte-for-byte: full capacity comes back
            TightClonePatches.Enabled = false;
            MeshData vanilla = src.Clone();
            if (vanilla.CustomFloats.Values.Length != 1000 || vanilla.CustomInts.Values.Length != 2048)
                throw new Exception("the disabled path is not vanilla");
            TightClonePatches.Enabled = true;
            TightClonePatches.ResetStats();
        });

        Check("the HUD raster keeps one texture size while its numbers jitter", () =>
        {
            // LoadOrUpdateCairoTexture deletes and recreates the GL texture on ANY size
            // change, and the widest HUD line holds live numbers - so the surface size must
            // absorb normal width jitter, or every rebuild stalls in the driver again.
            (int w, int h) = DebugHud.NextSurfaceSize(0, 0, 390, 610, 17);
            if (w != 448 || h != 612)
                throw new Exception($"first size not stepped up: {w}x{h}");

            // number jitter within the step lands on the exact same size (the actual fix)
            if (DebugHud.NextSurfaceSize(w, h, 401, 610, 17) != (w, h))
                throw new Exception("width jitter changed the surface size");
            if (DebugHud.NextSurfaceSize(w, h, 390, 594, 17) != (w, h))
                throw new Exception("a shrunk text shrank the surface");

            // real growth still grows, monotonically
            (int w2, int h2) = DebugHud.NextSurfaceSize(w, h, 470, 680, 17);
            if (w2 < 470 || h2 < 680 || w2 < w || h2 < h)
                throw new Exception($"growth not honoured: {w2}x{h2}");

            // degenerate line height must not divide by zero
            DebugHud.NextSurfaceSize(0, 0, 8, 8, 0);
        });

        Check("pools that collide in the cache memo keep their own results", () =>
        {
            // The memo in front of the weak table is direct-mapped, so with more pools than
            // slots several pools share one. A slot trusted without the identity check would
            // hand one pool another pool's cache - and with it another pool's visibility.
            FastCuller.ForgetAllPools();
            FastCuller.Parallel = true;
            FrustumCulling culler = NewCuller();

            var pools = new List<MeshDataPool>();
            for (int i = 0; i < 1500; i++) pools.Add(NewPool()); // NewPool builds identical pools
            foreach (MeshDataPool p in pools) FastCuller.Cull(p, culler, EnumFrustumCullMode.CullNormal);

            FastCuller.Parallel = false;
            MeshDataPool reference = NewPool();
            reference.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
            FastCuller.Parallel = true;
            int want = reference.indicesGroupsCount;
            if (want == 0) throw new Exception("reference pool drew nothing - the test proves nothing");

            for (int i = 0; i < pools.Count; i++)
                if (pools[i].indicesGroupsCount != want)
                    throw new Exception($"pool {i}: {pools[i].indicesGroupsCount} ranges, expected {want}");

            // Second pass, same stage: every call is a no-op that must leave the previous
            // result standing rather than overwrite it from a neighbour's slot.
            foreach (MeshDataPool p in pools) FastCuller.Cull(p, culler, EnumFrustumCullMode.CullNormal);
            for (int i = 0; i < pools.Count; i++)
                if (pools[i].indicesGroupsCount != want)
                    throw new Exception($"pool {i} lost its result on the second pass: {pools[i].indicesGroupsCount}");

            FastCuller.ForgetAllPools();
        });

        Check("the vector sweep kernel decides exactly like the scalar one", () =>
        {
            // The sweep has two implementations of one decision - four parts per instruction on
            // AVX, one at a time otherwise - and only one of them runs on any given machine. The
            // benchmark compares both against vanilla over realistic pools; this pins down the
            // two places where "same arithmetic" is easy to get subtly wrong:
            //
            //   * the tail. A bucket whose length is not a multiple of four finishes in the
            //     scalar loop, so every bucket length modulo four has to be exercised - hence
            //     the odd part counts below rather than one big pool.
            //   * NaN. Vanilla's Plane.AABBisOutside returns dist < 0, so a NaN distance counts
            //     as INSIDE. The vector path has to spell that as AndNot of the (d < 0) mask;
            //     the obvious (d >= 0) would silently cull those parts instead.
            if (!FastCuller.VectorAvailable) return; // nothing to compare on this CPU

            AccessTools.FieldRef<FrustumCulling, Plane[]> frustumRef =
                AccessTools.FieldRefAccess<FrustumCulling, Plane[]>("frustum");

            bool savedParallel = FastCuller.Parallel;
            bool savedPoolBox = FastCuller.PoolLevelCulling;
            FastCuller.Parallel = false;

            var modes = new[]
            {
                EnumFrustumCullMode.CullNormal, EnumFrustumCullMode.CullInstant,
                EnumFrustumCullMode.CullInstantShadowPassNear, EnumFrustumCullMode.CullInstantShadowPassFar,
                EnumFrustumCullMode.NoCull
            };

            int compared = 0, drew = 0;
            try
            {
                foreach (bool poolBox in new[] { false, true })
                foreach (int poison in new[] { -1, 1, 4 })
                foreach (int count in new[] { 1, 2, 3, 5, 7, 13, 17, 31, 33, 49, 50, 51, 127, 200 })
                {
                    FastCuller.PoolLevelCulling = poolBox;
                    FrustumCulling culler = NewCuller();
                    if (poison >= 0)
                    {
                        frustumRef(culler)[poison].normalY = double.NaN;
                        // the plane cache keys on this; without the bump a worker thread would
                        // happily reuse the clean planes and the test would prove nothing
                        FastCuller.FrustumGeneration++;
                    }

                    foreach (EnumFrustumCullMode mode in modes)
                    {
                        FastCuller.ForgetAllPools();
                        MeshDataPool vanilla = OddPool(count);
                        MeshDataPool withSimd = OddPool(count);
                        MeshDataPool withoutSimd = OddPool(count);

                        // MeshDataPool.FrustumCull is patched in this process, so calling it
                        // would route straight back into FastCuller and compare the mod with
                        // itself - which is exactly what an earlier version of this check did,
                        // and it passed a mutation that broke the NaN rule. Enabled=false is
                        // what safemode uses to make the prefix hand the sweep back.
                        FastCuller.Enabled = false;
                        vanilla.FrustumCull(culler, mode);
                        FastCuller.Enabled = true;

                        FastCuller.VectorCulling = true;
                        FastCuller.Cull(withSimd, culler, mode);
                        FastCuller.VectorCulling = false;
                        FastCuller.Cull(withoutSimd, culler, mode);
                        FastCuller.VectorCulling = true;

                        string want = Runs(vanilla), simd = Runs(withSimd), scalar = Runs(withoutSimd);
                        compared++;
                        if (vanilla.indicesGroupsCount > 0) drew++;
                        if (simd != want)
                            throw new Exception($"vector kernel differs from vanilla (n={count}, {mode}, "
                                              + $"poolbox={poolBox}, nan-plane={poison})\n        vanilla {want}\n        vector  {simd}");
                        if (scalar != want)
                            throw new Exception($"scalar kernel differs from vanilla (n={count}, {mode}, "
                                              + $"poolbox={poolBox}, nan-plane={poison})\n        vanilla {want}\n        scalar  {scalar}");
                    }
                }
            }
            finally
            {
                FastCuller.Enabled = true;
                FastCuller.VectorCulling = FastCuller.VectorAvailable;
                FastCuller.Parallel = savedParallel;
                FastCuller.PoolLevelCulling = savedPoolBox;
                FastCuller.ForgetAllPools();
            }

            // A run in which nothing was ever visible would pass on empty output alone.
            if (drew < compared / 4)
                throw new Exception($"only {drew} of {compared} cases drew anything - this proves too little");
        });

        Check("shadow far-LOD skip only ever removes LOD 3, and only when near", () =>
        {
            // The claim being tested: turning the option on draws strictly less, and every
            // part that disappears is a LOD 3 stand-in whose detailed counterpart stays in the
            // map. Anything else would be a quality loss.
            //
            // Note it is *triangles* that must fall, not draw ranges: dropping parts out of
            // the middle of a merged run splits that run, so the range count can rise even as
            // less geometry is drawn. Measuring ranges here reported "nothing was skipped".
            FastCuller.ForgetAllPools();
            FrustumCulling culler = NewCuller();          // lod2BiasSq = 400^2, pool sits well inside
            FastCuller.Parallel = false;

            MeshDataPool pool = NewPool();
            var locations = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(pool);

            FastCuller.ShadowSkipRedundantLod = false;
            pool.FrustumCull(culler, EnumFrustumCullMode.CullInstantShadowPassNear);
            List<(int, int)> before = Drawn(pool);
            int trisBefore = pool.RenderedTriangles;

            FastCuller.ShadowSkipRedundantLod = true;
            FastCuller.Invalidate(pool);
            pool.FrustumCull(culler, EnumFrustumCullMode.CullInstantShadowPassNear);
            List<(int, int)> after = Drawn(pool);
            int trisAfter = pool.RenderedTriangles;
            FastCuller.ShadowSkipRedundantLod = false;

            if (trisAfter >= trisBefore)
                throw new Exception($"nothing was saved: {trisBefore} triangles before, {trisAfter} after");

            foreach (ModelDataPoolLocation loc in locations)
            {
                bool drawnBefore = Covers(before, loc.IndicesStart * 4, (loc.IndicesEnd - loc.IndicesStart) * 4);
                bool drawnAfter = Covers(after, loc.IndicesStart * 4, (loc.IndicesEnd - loc.IndicesStart) * 4);

                if (drawnAfter && !drawnBefore)
                    throw new Exception($"LOD {loc.LodLevel} part appeared that vanilla did not draw");
                if (drawnBefore && !drawnAfter && loc.LodLevel != 3)
                    throw new Exception($"dropped a LOD {loc.LodLevel} part - only the LOD 3 stand-in may go");
            }

            FastCuller.Parallel = true;
            FastCuller.ForgetAllPools();
        });

        Check("appended parts render identically without a cache rebuild", () =>
        {
            // The append fast path skips rebuilding the spatial index, so the new parts are
            // swept outside the grid. If that path disagreed with a full rebuild by even one
            // part, freshly tesselated chunks would be missing from the world.
            FastCuller.ForgetAllPools();
            FastCuller.Parallel = false;
            FrustumCulling culler = NewCuller();

            MeshDataPool incremental = NewPool();
            MeshDataPool reference = NewPool();
            var incLocs = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(incremental);
            var refLocs = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(reference);

            incremental.FrustumCull(culler, EnumFrustumCullMode.CullNormal); // builds the grid
            long incrementalRebuilds = 0;

            var rnd = new Random(17);
            for (int round = 0; round < 12; round++)
            {
                for (int i = 0; i < 3; i++)
                {
                    var loc = new ModelDataPoolLocation
                    {
                        IndicesStart = 100000 + (round * 3 + i) * 300,
                        IndicesEnd = 100000 + (round * 3 + i) * 300 + 300,
                        LodLevel = rnd.Next(0, 4),
                        FrustumCullSphere = Sphere.BoundingSphereForCube(
                            rnd.Next(-6, 7) * 32, 128, rnd.Next(-6, 7) * 32, 32)
                    };
                    incLocs.Add(loc);
                    refLocs.Add(loc);
                    FastCuller.NoteAppended(incremental);   // the append fast path
                    FastCuller.Invalidate(reference);       // the full rebuild
                }

                foreach (EnumFrustumCullMode mode in new[]
                {
                    EnumFrustumCullMode.CullNormal,
                    EnumFrustumCullMode.CullInstantShadowPassNear,
                    EnumFrustumCullMode.CullInstantShadowPassFar,
                    EnumFrustumCullMode.NoCull
                })
                {
                    // count rebuilds for the incremental pool only - the reference is
                    // invalidated on purpose and would swamp the figure
                    long before = FastCuller.StatRebuilds;
                    incremental.FrustumCull(culler, mode);
                    incrementalRebuilds += FastCuller.StatRebuilds - before;

                    reference.FrustumCull(culler, mode);
                    FastCuller.Invalidate(reference); // force the reference to rebuild every time

                    if (incremental.RenderedTriangles != reference.RenderedTriangles)
                        throw new Exception($"{mode}: {incremental.RenderedTriangles} triangles vs {reference.RenderedTriangles}");

                    List<(int, int)> a = Drawn(incremental), b = Drawn(reference);
                    if (a.Count != b.Count) throw new Exception($"{mode}: {a.Count} ranges vs {b.Count}");
                    for (int i = 0; i < a.Count; i++)
                        if (a[i] != b[i]) throw new Exception($"{mode}: range {i} differs, {a[i]} vs {b[i]}");
                }
            }

            // and it has to have actually taken the fast path, or the test proved nothing:
            // 36 appends across 12 rounds, all of them inside the overflow limit
            if (incrementalRebuilds != 0)
                throw new Exception($"{incrementalRebuilds} rebuilds - the append path was not used");

            FastCuller.Parallel = true;
            FastCuller.ForgetAllPools();
        });

        Check("a rebuild after appends does not run off a stale array", () =>
        {
            // The 1.12.0 crash: Extend grows only the arrays it writes, so after an append the
            // rest are still short. A rebuild that checked one array as a proxy for all then
            // skipped the reallocation and wrote past the end of Scratch - an
            // IndexOutOfRangeException in the middle of the shadow pass.
            //
            // The first version of the append test never reached this: it appended 36 parts,
            // under the overflow limit, so no rebuild ever followed an Extend.
            FastCuller.ForgetAllPools();
            FastCuller.Parallel = false;
            FrustumCulling culler = NewCuller();

            MeshDataPool pool = NewPool();
            var locs = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(pool);
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal); // sizes the arrays for 64 parts

            // append far past the overflow limit, so the sweep is forced back into a rebuild
            for (int i = 0; i < 400; i++)
            {
                locs.Add(new ModelDataPoolLocation
                {
                    IndicesStart = 200000 + i * 300,
                    IndicesEnd = 200000 + i * 300 + 300,
                    LodLevel = i % 4,
                    FrustumCullSphere = Sphere.BoundingSphereForCube((i % 9) * 32, 128, (i / 9) * 32, 32)
                });
                FastCuller.NoteAppended(pool);

                // cull after every append: somewhere in here the overflow limit is crossed and
                // the next sweep rebuilds on arrays Extend left behind
                pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
                pool.FrustumCull(culler, EnumFrustumCullMode.CullInstantShadowPassFar);
            }

            if (pool.indicesGroupsCount == 0) throw new Exception("pool drew nothing after 400 appends");

            // and the result must still match a pool that only ever rebuilt
            MeshDataPool reference = NewPool();
            var refLocs = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(reference);
            refLocs.Clear();
            foreach (ModelDataPoolLocation l in locs) refLocs.Add(l);
            FastCuller.Invalidate(reference);
            reference.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);

            if (pool.RenderedTriangles != reference.RenderedTriangles)
                throw new Exception($"{pool.RenderedTriangles} triangles vs {reference.RenderedTriangles} after regrowth");

            FastCuller.Parallel = true;
            FastCuller.ForgetAllPools();
        });

        Check("random append/invalidate/remove sequences stay correct", () =>
        {
            // Two crashes came out of cache array sizing, and both slipped through tests that
            // exercised one specific sequence. A pool's history is append, insert, remove and
            // cull in any order, so this drives that shape randomly and compares every step
            // against a pool that only ever rebuilds. It reproduces both shipped crashes.
            FastCuller.ForgetAllPools();
            FastCuller.Parallel = false;
            FrustumCulling culler = NewCuller();
            long incInsertsBefore = FastCuller.StatIncInserts;

            var modes = new[]
            {
                EnumFrustumCullMode.CullNormal,
                EnumFrustumCullMode.CullInstantShadowPassNear,
                EnumFrustumCullMode.CullInstantShadowPassFar,
                EnumFrustumCullMode.CullInstant,
                EnumFrustumCullMode.NoCull
            };

            var rnd = new Random(4711);
            MeshDataPool live = NewPool();
            MeshDataPool reference = NewPool();
            var liveLocs = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(live);
            var refLocs = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(reference);
            refLocs.Clear();
            foreach (ModelDataPoolLocation l in liveLocs) refLocs.Add(l);

            int nextIndices = 500000;
            for (int step = 0; step < 3000; step++)
            {
                int action = rnd.Next(100);
                if (action < 70 && liveLocs.Count < 480)
                {
                    // append - the fast path
                    int count = 1 + rnd.Next(4);
                    for (int i = 0; i < count; i++)
                    {
                        var loc = new ModelDataPoolLocation
                        {
                            IndicesStart = nextIndices,
                            IndicesEnd = nextIndices + 300,
                            LodLevel = rnd.Next(0, 5),
                            Hide = rnd.Next(20) == 0,
                            FrustumCullSphere = Sphere.BoundingSphereForCube(
                                rnd.Next(-8, 9) * 32, rnd.Next(3, 6) * 32, rnd.Next(-8, 9) * 32, 32)
                        };
                        nextIndices += 300;
                        liveLocs.Add(loc);
                        refLocs.Add(loc);
                        FastCuller.NoteAppended(live);
                    }
                }
                else if (action < 85 && liveLocs.Count > 8 && liveLocs.Count < 480)
                {
                    // insert in the middle - the incremental path since 1.30.0.
                    // Bounded like the appends: this test adds locations directly, bypassing
                    // TryAdd, which in the game refuses past MaxPartsPerPool - and the pool's
                    // draw range scratch arrays are sized for exactly that many.
                    int at = rnd.Next(liveLocs.Count);
                    var loc = new ModelDataPoolLocation
                    {
                        IndicesStart = nextIndices,
                        IndicesEnd = nextIndices + 300,
                        LodLevel = rnd.Next(0, 5),
                        FrustumCullSphere = Sphere.BoundingSphereForCube(
                            rnd.Next(-8, 9) * 32, 128, rnd.Next(-8, 9) * 32, 32)
                    };
                    nextIndices += 300;
                    liveLocs.Insert(at, loc);
                    refLocs.Insert(at, loc);
                    FastCuller.NoteInserted(live, loc);     // the incremental insert path
                }
                else if (liveLocs.Count > 8)
                {
                    // remove - also a rebuild
                    int at = rnd.Next(liveLocs.Count);
                    liveLocs.RemoveAt(at);
                    refLocs.RemoveAt(at);
                    FastCuller.Invalidate(live);
                }

                EnumFrustumCullMode mode = modes[rnd.Next(modes.Length)];
                live.FrustumCull(culler, mode);
                FastCuller.Invalidate(reference);
                reference.FrustumCull(culler, mode);

                if (live.RenderedTriangles != reference.RenderedTriangles)
                    throw new Exception($"step {step} ({mode}, {liveLocs.Count} parts): " +
                                        $"{live.RenderedTriangles} triangles vs {reference.RenderedTriangles}");

                List<(int, int)> a = Drawn(live), b = Drawn(reference);
                if (a.Count != b.Count)
                    throw new Exception($"step {step} ({mode}): {a.Count} ranges vs {b.Count}");
                for (int i = 0; i < a.Count; i++)
                    if (a[i] != b[i]) throw new Exception($"step {step} ({mode}): range {i} differs");
            }

            // "passed" must also mean "the incremental path actually ran" - a silent
            // fallback to rebuilds would sail through the comparison (the dead-patch lesson)
            long incInserts = FastCuller.StatIncInserts - incInsertsBefore;
            if (incInserts < 100)
                throw new Exception($"only {incInserts} incremental inserts in 3000 steps - the fast path is not being taken");

            FastCuller.Parallel = true;
            FastCuller.ForgetAllPools();
        });

        Check("TryAdd / RemoveLocation invalidate the cache", () =>
        {
            MeshDataPool pool = NewPool();
            var locations = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(pool);

            FrustumCulling culler = NewCuller();
            pool.FrustumCull(culler, EnumFrustumCullMode.NoCull);
            int before = pool.indicesGroupsCount;

            locations.Add(new ModelDataPoolLocation
            {
                IndicesStart = 100000, IndicesEnd = 100300,
                FrustumCullSphere = Sphere.BoundingSphereForCube(0, 128, 0, 32)
            });
            FastCuller.Invalidate(pool);

            pool.FrustumCull(culler, EnumFrustumCullMode.NoCull);
            if (pool.indicesGroupsCount != before + 1) throw new Exception("cache did not pick up the new part");
        });

        if (Environment.GetCommandLineArgs().Length > 1)
        {
            FrameStats.Reset();
            long t2 = System.Diagnostics.Stopwatch.Frequency / 1000;
            var rnd2 = new Random(3);
            for (int i = 0; i < 250; i++)
            {
                FrameStats.BeginFrame();
                FrameStats.AddStageTicks((int)Vintagestory.API.Client.EnumRenderStage.Opaque, (long)(t2 * 4.2));
                FrameStats.AddStageTicks((int)Vintagestory.API.Client.EnumRenderStage.ShadowFar, (long)(t2 * 1.35));
                FrameStats.AddStageTicks((int)Vintagestory.API.Client.EnumRenderStage.ShadowNear, (long)(t2 * 1.10));
                FrameStats.AddStageTicks((int)Vintagestory.API.Client.EnumRenderStage.OIT, (long)(t2 * 0.62));
                FrameStats.AddStageTicks((int)Vintagestory.API.Client.EnumRenderStage.Ortho, (long)(t2 * 0.41));
                FrameStats.AddStageTicks((int)Vintagestory.API.Client.EnumRenderStage.Done, (long)(t2 * 0.35));
                FrameStats.AddCullTicks((long)(t2 * 1.98));
                FrameStats.AddGameTickTicks((long)(t2 * 1.42));
                FrameStats.AddUploadMs(0.38);
                System.Threading.Thread.Sleep(1);
            }
            FrameStats.BeginFrame();
            Console.WriteLine("\n--- HUD preview ---");
            // stamped by hand: this runner is not the mod assembly and carries no build stamp,
            // but the preview is exactly where the real title width has to be visible
            Console.WriteLine(DebugHud.Compose("komet " + KometVersion.Compose("1.0.0", "260830.1917"),
                3412, 1536, 512L * 1048576, 214, 0.11f, 41024, null));
            Console.WriteLine();
            Console.WriteLine(DebugHud.Compose("vanilla 1.5.0", 3412, 1536, 512L * 1048576, 214, 0.11f, 41024, null));
            Console.WriteLine("--- end ---\n");
        }

        // Every check above proves a patch class *works*. None of them proves the mod ever
        // calls it - which is exactly how shadow throttling shipped dead in 1.6 through 1.8.1:
        // the class was written, configured and read by the HUD, but ApplyPatches never
        // invoked it. This check closes that gap for every patch class, present and future.
        Check("safemode hands the sweep back to vanilla mid-session", () =>
        {
            // The point of safemode is answering "is the mod drawing this wrong?" while the
            // artefact is on screen. That only works if disabling really reaches vanilla - a
            // prefix that still ran FastCuller would make the answer a lie in the dangerous
            // direction.
            FastCuller.ForgetAllPools();
            FrustumCulling culler = NewCuller();
            MeshDataPool pool = NewPool();

            FastCuller.Enabled = true;
            long sweepsBefore = FastCuller.StatSweeps;
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
            if (FastCuller.StatSweeps == sweepsBefore) throw new Exception("enabled but FastCuller never ran");
            int viaFast = pool.indicesGroupsCount;

            FastCuller.Enabled = false;
            sweepsBefore = FastCuller.StatSweeps;
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
            if (FastCuller.StatSweeps != sweepsBefore)
                throw new Exception("safemode still routed the sweep through FastCuller");
            int viaVanilla = pool.indicesGroupsCount;
            if (viaVanilla == 0 && viaFast > 0)
                throw new Exception("vanilla path drew nothing where FastCuller drew something");

            // and back on again without a rebuild of the world
            FastCuller.Enabled = true;
            sweepsBefore = FastCuller.StatSweeps;
            pool.FrustumCull(culler, EnumFrustumCullMode.CullNormal);
            if (FastCuller.StatSweeps == sweepsBefore) throw new Exception("re-enabling did not take");

            FastCuller.ForgetAllPools();
        });

        Check("the report names every setting that differs from the defaults, and no others", () =>
        {
            // The report's job is to make a session judgeable without asking follow-up
            // questions, and "which settings were actually live?" is the follow-up that has
            // been needed most. Reflected rather than hand-listed precisely so that the
            // setting added last is covered too - which this checks by picking properties at
            // runtime rather than naming any.
            var cfg = new KometConfig();
            if (KometModSystem.ConfigDelta(cfg) != null)
                throw new Exception("a default config reported differences against itself");

            foreach (System.Reflection.PropertyInfo p in typeof(KometConfig).GetProperties())
            {
                if (!p.CanRead || !p.CanWrite) continue;
                object original = p.GetValue(cfg);
                object changed = p.PropertyType == typeof(bool) ? (object)!(bool)original
                               : p.PropertyType == typeof(int) ? (object)((int)original + 7)
                               : p.PropertyType == typeof(double) ? (object)((double)original + 7.0)
                               : null;
                if (changed == null) continue;

                p.SetValue(cfg, changed);
                string delta = KometModSystem.ConfigDelta(cfg);
                p.SetValue(cfg, original);

                if (delta == null || !delta.Contains(p.Name))
                    throw new Exception($"changing {p.Name} was not reported: '{delta}'");
                if (delta.Contains(", "))
                    throw new Exception($"changing {p.Name} alone reported several settings: '{delta}'");
            }
        });

        Check("a config from another layout version is regenerated, not carried forward", () =>
        {
            // The whole point: a default changed in the source has to reach an install that
            // already has the file. Without this, "the default is now X" silently means
            // "unchanged for everyone who has played before", which is how a shadow fix
            // stayed half-applied.
            if (!KometModSystem.ShouldRegenerate("1", "2"))
                throw new Exception("kept a config written by an older layout");
            if (!KometModSystem.ShouldRegenerate(null, "2"))
                throw new Exception("kept a config with no version - that predates the check");
            if (!KometModSystem.ShouldRegenerate("", "2"))
                throw new Exception("kept a config with an empty version");
            if (!KometModSystem.ShouldRegenerate("3", "2"))
                throw new Exception("kept a config from a NEWER layout - it may hold keys this build cannot honour");
            // the layout used to be the mod version; those files have to go too
            if (!KometModSystem.ShouldRegenerate("1.52.0", KometConfig.Current))
                throw new Exception("kept a config still versioned by the old mod-version scheme");

            // and it must not churn the file on every single start
            if (KometModSystem.ShouldRegenerate("2", "2"))
                throw new Exception("regenerated a config that is already current");
            if (KometModSystem.ShouldRegenerate(KometConfig.Current, KometConfig.Current))
                throw new Exception("regenerated a config that this very build just wrote");

            // The stored value comes out of a file a user can edit and ends up in a path.
            if (KometModSystem.BackupTag("../../evil") != "....evil")
                throw new Exception("a backup tag kept path separators: " + KometModSystem.BackupTag("../../evil"));
            if (KometModSystem.BackupTag("") != "alt" || KometModSystem.BackupTag(null) != "alt")
                throw new Exception("an empty version produced an empty backup suffix");
            if (KometModSystem.BackupTag("/\\:*?").Length != 3)
                throw new Exception("a version of nothing but separators did not fall back");
            if (KometModSystem.BackupTag(new string('9', 400)).Length != 16)
                throw new Exception("a very long version was not truncated");
            if (KometModSystem.BackupTag("1.52.0") != "1.52.0")
                throw new Exception("an ordinary version was mangled");
        });

        Check("the build stamp identifies the DLL, and its absence is not a crash", () =>
        {
            // A field log has to answer "which build was that". The version cannot answer it -
            // many builds share 1.0.0 - so the compile minute is shown next to it.
            if (KometVersion.Compose("1.0.0", "260830.1917") != "1.0.0 (b260830.1917)")
                throw new Exception("version line reads " + KometVersion.Compose("1.0.0", "260830.1917"));
            if (KometVersion.StampFrom("1.0.0+260830.1917") != "260830.1917")
                throw new Exception("did not read the stamp out of the informational version");

            // Everything that compiles these sources without the stamping target - this very
            // check runner, the bench - must still show a sane line instead of throwing.
            if (KometVersion.StampFrom("1.0.0") != null)
                throw new Exception("invented a stamp for an unstamped assembly");
            if (KometVersion.StampFrom("1.0.0+") != null)
                throw new Exception("an empty stamp is not a stamp");
            if (KometVersion.StampFrom(null) != null || KometVersion.StampFrom("") != null)
                throw new Exception("a missing informational version did not yield null");
            if (KometVersion.Compose("1.0.0", null) != "1.0.0" || KometVersion.Compose("1.0.0", "") != "1.0.0")
                throw new Exception("an unstamped build did not fall back to the bare version");
        });

        Check("every patch class is reachable from KometModSystem", () =>
        {
            var referenced = new HashSet<Type>();
            var toScan = new List<Type> { typeof(KometModSystem) };
            toScan.AddRange(typeof(KometModSystem).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

            foreach (Type t in toScan)
            foreach (MethodBase m in GetAllMethods(t))
            {
                IEnumerable<KeyValuePair<OpCode, object>> body;
                try { body = PatchProcessor.ReadMethodBody(m); }
                catch { continue; } // abstract / extern / no IL
                foreach (var ins in body)
                {
                    // a direct call into the class, or a typeof() handed to CreateClassProcessor
                    if (ins.Value is MethodBase called && called.DeclaringType != null) referenced.Add(called.DeclaringType);
                    else if (ins.Value is Type token) referenced.Add(token);
                }
            }

            var orphans = new List<string>();
            foreach (Type patch in PatchClasses())
                if (!referenced.Contains(patch)) orphans.Add(patch.Name);

            if (orphans.Count > 0)
                throw new Exception($"never called by the mod: {string.Join(", ", orphans)}");
        });

        Check("skipped-frame shadow matrix compensation is exact", () =>
        {
            // The retained far shadow map is sampled in camera-relative coordinates. When the
            // camera moves by delta during a skipped frame, the compensated matrix M' must
            // satisfy M' * p == M * (p + delta) - same texel, no flicker. Verified against a
            // full reference multiply for a matrix with rotation, scale and translation.
            var rnd = new Random(11);
            float[] m = new float[16];
            double a = 0.7;
            // ortho-ish projection * rotation * translation, column major
            m[0] = (float)(0.01 * Math.Cos(a)); m[4] = 0f;      m[8] = (float)(0.01 * Math.Sin(a)); m[12] = 0.45f;
            m[1] = 0f;                          m[5] = 0.013f;  m[9] = 0f;                          m[13] = 0.52f;
            m[2] = (float)(-0.02 * Math.Sin(a)); m[6] = 0f;     m[10] = (float)(0.02 * Math.Cos(a)); m[14] = 0.5f;
            m[3] = 0f;                          m[7] = 0f;      m[11] = 0f;                         m[15] = 1f;

            float[] outM = new float[16];
            for (int trial = 0; trial < 50; trial++)
            {
                double dx = (rnd.NextDouble() - 0.5) * 40;
                double dy = (rnd.NextDouble() - 0.5) * 40;
                double dz = (rnd.NextDouble() - 0.5) * 40;
                ShadowThrottlePatches.OffsetShadowMatrix(m, outM, dx, dy, dz);

                double px = (rnd.NextDouble() - 0.5) * 500;
                double py = (rnd.NextDouble() - 0.5) * 500;
                double pz = (rnd.NextDouble() - 0.5) * 500;

                for (int row = 0; row < 4; row++)
                {
                    double want = m[row] * (px + dx) + m[4 + row] * (py + dy) + m[8 + row] * (pz + dz) + m[12 + row];
                    double got = outM[row] * px + outM[4 + row] * py + outM[8 + row] * pz + outM[12 + row];
                    if (Math.Abs(want - got) > 1e-3)
                        throw new Exception($"row {row}: {got} != {want} (delta {want - got})");
                }
            }

            // and the linear part must be untouched - only the translation column may move
            for (int i = 0; i < 12; i++)
                if (outM[i] != m[i]) throw new Exception($"linear element {i} changed");
        });

        Check("inflow brake throttles for real and never stalls delivery", () =>
        {
            const int low = 400, high = 2000, baseCols = 4, baseTick = 20;

            double prevFactor = double.MaxValue;
            double prevRate = double.MaxValue;
            for (int backlog = 0; backlog <= 8000; backlog += 25)
            {
                double f = InflowBrake.FactorFor(backlog, low, high);
                if (f > 1.0) throw new Exception($"backlog {backlog}: factor {f} above full speed");
                if (f <= 0) throw new Exception($"backlog {backlog}: factor {f} would stall delivery");
                if (f > prevFactor) throw new Exception($"backlog {backlog}: factor rose from {prevFactor} to {f}");
                prevFactor = f;

                InflowBrake.KnobsFor(f, baseCols, baseTick, out int cols, out int tick);
                if (cols < 1) throw new Exception($"backlog {backlog}: {cols} columns would stall delivery");
                if (cols > baseCols) throw new Exception($"backlog {backlog}: {cols} columns exceeds base");
                if (tick < baseTick) throw new Exception($"backlog {backlog}: tick {tick} faster than vanilla");
                if (tick > 500) throw new Exception($"backlog {backlog}: tick {tick} beyond the cap");

                // columns per second is what actually paces delivery, and it has to fall
                // monotonically - this is what a column count alone could not do, because it
                // bottoms out at 1 and still allows over a thousand chunks a second
                double rate = cols * 1000.0 / tick;
                if (rate > prevRate + 1e-9) throw new Exception($"backlog {backlog}: rate rose to {rate}/s");
                prevRate = rate;
            }

            if (InflowBrake.FactorFor(0, low, high) != 1.0) throw new Exception("idle client is throttled");
            if (InflowBrake.FactorFor(low, low, high) != 1.0) throw new Exception("throttling starts too early");
            if (InflowBrake.FactorFor(high, low, high) >= 1.0) throw new Exception("no brake at high water");

            // the full brake has to be a real reduction, not a rounding artefact
            InflowBrake.KnobsFor(InflowBrake.FactorFor(high, low, high), baseCols, baseTick, out int c2, out int t2);
            double fullRate = baseCols * 1000.0 / baseTick;
            double brakedRate = c2 * 1000.0 / t2;
            if (brakedRate > fullRate * 0.2)
                throw new Exception($"full brake only reaches {brakedRate:F0}/s of {fullRate:F0}/s");

            // The deep segment: a queue growing PAST "fully braked" is proof the brake was
            // not hard enough. Measured in the field: at high water the old floor still let
            // 850 chunks/s through (1 column/100 ms x4 local x ~21 chunks of a mountain
            // column) against 276/s digested. Three times past high water the effective rate
            // must be a small fraction of the old floor.
            InflowBrake.KnobsFor(InflowBrake.FactorFor(high, low, high), baseCols, baseTick, out int cHigh, out int tHigh);
            InflowBrake.KnobsFor(InflowBrake.FactorFor(high * 3, low, high), baseCols, baseTick, out int cDeep, out int tDeep);
            double highRate = cHigh * 1000.0 / tHigh;
            double deepRate = cDeep * 1000.0 / tDeep;
            if (deepRate > highRate * 0.25)
                throw new Exception($"deep brake reaches only {deepRate:F1} col/s against {highRate:F1} at high water");
            if (tDeep != 500) throw new Exception($"deep brake should sit at the 500 ms tick cap, got {tDeep}");
            if (cDeep < 1) throw new Exception("deep brake stalled delivery");
            // and it keeps falling BETWEEN high water and deep water, not just at the ends
            double fMid = InflowBrake.FactorFor(high * 2, low, high);
            if (fMid >= InflowBrake.FactorFor(high, low, high) || fMid <= InflowBrake.FactorFor(high * 3, low, high))
                throw new Exception($"deep segment is not a slope: factor {fMid} at 2x high water");

            // degenerate configuration must not divide by zero or invert
            InflowBrake.KnobsFor(InflowBrake.FactorFor(500, 400, 400), 1, 20, out int c3, out int t3);
            if (c3 < 1 || t3 < 20) throw new Exception("degenerate band misbehaves");

            // The arrival-rate term: the queue brake's blind spot is the full-rate window at
            // region entry (queue empty, thousands of columns accepted before it fills).
            // Rate braking must engage as soon as arrivals outrun digestion - and never
            // punish a trickle or a cold start.
            if (InflowBrake.RateFactorFor(50, 300) != 1.0)
                throw new Exception("a trickle must never be rate-braked");
            if (InflowBrake.RateFactorFor(199, 0) != 1.0)
                throw new Exception("cold start with low flow must run free");
            double atMatch = InflowBrake.RateFactorFor(450, 300);
            if (atMatch < 0.99)
                throw new Exception($"arrivals at 1.5x digestion should run free, got {atMatch}");
            double flood = InflowBrake.RateFactorFor(1648, 275);
            if (flood > 0.30)
                throw new Exception($"the measured 1648/275 flood only braked to {flood:P0}");
            if (InflowBrake.RateFactorFor(100000, 275) < 0.009)
                throw new Exception("rate brake must never stall delivery entirely");
            double prevRate2 = double.MaxValue;
            for (int a = 200; a <= 5000; a += 100)
            {
                double f = InflowBrake.RateFactorFor(a, 300);
                if (f > prevRate2 + 1e-12) throw new Exception($"rate factor rose at arrival {a}");
                prevRate2 = f;
            }
        });

        Check("stress test cancels drift via neighbour baselines and restores state", () =>
        {
            // The arithmetic first, on hand-made slices: baselines drift 10 -> 14 ms while
            // system "a" adds +4/+5 in its two rounds and "b" adds nothing. The paired
            // estimator must report a = +4.50 +-0.50 and b = 0.00 - a global-mean estimator
            // would smear the drift into both.
            var sys = new List<StressTest.Phase>
            {
                new StressTest.Phase { Name = "a aus" },
                new StressTest.Phase { Name = "b aus" },
            };
            int[] plan = { -1, 0, -1, 1, -1, 0, -1, 1, -1 };
            double[] avgs = { 10, 15, 12, 12.5, 13, 18.5, 14, 14.5, 15 };
            var slices = new List<StressTest.Slice>();
            for (int i = 0; i < plan.Length; i++)
                slices.Add(new StressTest.Slice { System = plan[i], Frames = 1, SumMs = avgs[i] });

            string rep = StressTest.BuildReport(slices, plan, sys);
            var ci = System.Globalization.CultureInfo.CurrentCulture;
            string wantA = "a aus: delta +" + 4.5.ToString("F2", ci);
            string wantASpread = "+-" + 0.5.ToString("F2", ci);
            string wantB = "b aus: delta +" + 0.0.ToString("F2", ci);
            if (!rep.Contains(wantA) || !rep.Contains(wantASpread))
                throw new Exception($"drift not cancelled for a ('{wantA}' / '{wantASpread}'):\n{rep}");
            if (!rep.Contains(wantB))
                throw new Exception($"drift leaked into b ('{wantB}'):\n{rep}");

            // The swap/shadow split, on the case that motivated it: a system whose whole cost
            // is driver back-pressure while the shadow stage gets CHEAPER. Frame-time alone
            // cannot tell that from CPU work, and reading it as CPU work is how "safemode is
            // faster" stayed unexplained. Same neighbour-baseline arithmetic, so a drifting
            // swap baseline has to cancel here too: baselines climb 1 -> 3 ms of swap.
            double[] swaps = { 1, 3.5, 1.5, 1.75, 2, 4.5, 2.5, 2.75, 3 };
            double[] shadows = { 5, 4.6, 5, 5.0, 5, 4.6, 5, 5.0, 5 };
            var split = new List<StressTest.Slice>();
            for (int i = 0; i < plan.Length; i++)
                split.Add(new StressTest.Slice
                {
                    System = plan[i], Frames = 1, SumMs = avgs[i],
                    SumSwapMs = swaps[i], SumShadowMs = shadows[i],
                });

            string rep2 = StressTest.BuildReport(split, plan, sys);
            // a: swap 3.5 vs (1+1.5)/2 = 1.25 -> +2.25; 4.5 vs (2+2.5)/2 = 2.25 -> +2.25
            string wantSplit = "[swap +" + 2.25.ToString("F2", ci) + ", schatten -" + 0.4.ToString("F2", ci) + "]";
            if (!rep2.Contains(wantSplit))
                throw new Exception($"swap/shadow split wrong (want '{wantSplit}'):\n{rep2}");
            // b costs nothing anywhere - the split must not invent a delta from the drift
            string wantQuiet = "[swap +" + 0.0.ToString("F2", ci) + ", schatten +" + 0.0.ToString("F2", ci) + "]";
            if (!rep2.Contains(wantQuiet))
                throw new Exception($"drift leaked into b's split (want '{wantQuiet}'):\n{rep2}");

            // And the accumulation itself, end to end: a system that costs 2 ms of swap and
            // nothing else has to come back out of a real run as exactly that. Without this the
            // arithmetic above is only ever tested on hand-made slices.
            bool costing = false;
            string splitReport = null;
            StressTest.Start(
                new List<StressTest.Phase> { new StressTest.Phase
                    { Name = "x", Enter = () => costing = true, Exit = () => costing = false } },
                2, 1, r => splitReport = r);
            long tf = 1_000_000, stepf = System.Diagnostics.Stopwatch.Frequency / 100;
            for (int i = 0; i < 1000 && StressTest.Running; i++)
            {
                tf += stepf;
                StressTest.Tick(tf, costing ? 3.0 : 1.0, 0.5);
            }
            if (StressTest.Running) { StressTest.Stop("test"); throw new Exception("split run did not finish"); }
            string wantRun = "[swap +" + 2.0.ToString("F2", ci) + ", schatten +" + 0.0.ToString("F2", ci) + "]";
            if (splitReport == null || !splitReport.Contains(wantRun))
                throw new Exception($"live swap accounting wrong (want '{wantRun}'):\n{splitReport}");

            // The schedule: every test slice must sit between two baselines, each system
            // once per round, one closing baseline.
            int[] sched = StressTest.BuildSchedule(3, 2);
            if (sched.Length != 2 * 3 * 2 + 1) throw new Exception($"schedule length {sched.Length}");
            if (sched[^1] != -1) throw new Exception("no closing baseline");
            for (int i = 0; i < sched.Length; i++)
                if (sched[i] >= 0 && (sched[i - 1] != -1 || sched[i + 1] != -1))
                    throw new Exception($"test slice {i} lacks a neighbouring baseline");

            // End-to-end sequencing: enters/exits balanced per round, live delta recovery
            // under a strong linear drift, and abort restores the active system.
            var events = new List<string>();
            string report = null;
            List<StressTest.Phase> Plan() => new()
            {
                new StressTest.Phase { Name = "a aus", Enter = () => events.Add("+a"), Exit = () => events.Add("-a") },
                new StressTest.Phase { Name = "b aus", Enter = () => events.Add("+b"), Exit = () => events.Add("-b") },
            };

            StressTest.Start(Plan(), 2, 3, r => report = r);
            long freq = System.Diagnostics.Stopwatch.Frequency;
            long t0 = 123456789, t = t0;
            double runSeconds = 13 * 2.0; // 13 slices at 2 s
            int active = -1;
            int guard = 40000;
            var track = Plan(); // not used; events come from the started instance's lambdas
            while (StressTest.Running && guard-- > 0)
            {
                active = events.Count == 0 ? -1
                    : events[^1] == "+a" ? 0 : events[^1] == "+b" ? 1 : -1;
                double drift = 10 + 10 * ((t - t0) / (double)freq) / runSeconds;
                double ms = drift + (active == 0 ? 4 : 0);
                t += (long)(ms * freq / 1000);
                StressTest.Tick(t);
            }
            if (guard <= 0) throw new Exception("stress test never finished");
            if (report == null) throw new Exception("no report produced");

            int opens = events.FindAll(e => e == "+a").Count;
            int closes = events.FindAll(e => e == "-a").Count;
            if (opens != 3 || closes != 3)
                throw new Exception($"system a entered {opens}x / exited {closes}x, expected 3/3");

            // +4 must survive a 10->20 ms drift; allow slack for slice-boundary attribution
            int at = report.IndexOf("a aus: delta +");
            if (at < 0) throw new Exception("no delta line for a:\n" + report);
            string num = report.Substring(at + "a aus: delta +".Length, 4);
            double recovered = double.Parse(num, System.Globalization.NumberStyles.Float, ci);
            if (Math.Abs(recovered - 4.0) > 0.3)
                throw new Exception($"recovered {recovered} instead of ~4.0 under drift:\n{report}");

            // abort mid-slice restores the active system
            events.Clear();
            StressTest.Start(Plan(), 2, 3, _ => { });
            long t2 = 987654321;
            guard = 40000;
            while (!events.Contains("+a") && guard-- > 0) { t2 += freq / 100; StressTest.Tick(t2); }
            StressTest.Stop("test");
            if (StressTest.Running) throw new Exception("stop did not stop");
            if (string.Join(",", events) != "+a,-a")
                throw new Exception($"abort left state as {string.Join(",", events)}");
        });

        Check("prebuild validation tolerates sporadic mismatches, stops at systematic ones", () =>
        {
            // A mismatch can be a legitimate in-between world change (lighting settling
            // after world join); only repetition proves a transcription bug, because the
            // fill plan is deterministic and a real bug would fail every window.
            WindowPrebuilder.StatValidationMismatches = 0;
            WindowPrebuilder.HardDisabled = false;
            WindowPrebuilder.Enabled = true;
            for (int i = 1; i < WindowPrebuilder.MismatchHardLimit; i++)
            {
                if (WindowPrebuilder.NoteMismatch())
                    throw new Exception($"disabled after only {i} mismatches");
                if (!WindowPrebuilder.Enabled || WindowPrebuilder.HardDisabled)
                    throw new Exception($"state flipped after only {i} mismatches");
            }
            if (!WindowPrebuilder.NoteMismatch())
                throw new Exception("limit reached but not disabled");
            if (WindowPrebuilder.Enabled || !WindowPrebuilder.HardDisabled)
                throw new Exception("hard disable did not set both flags");
            WindowPrebuilder.StatValidationMismatches = 0;
            WindowPrebuilder.HardDisabled = false;
            WindowPrebuilder.Enabled = false; // no game in this process
        });

        Check("occlusion teleport burst suspends the rate limit, walking does not", () =>
        {
            var c = new Vintagestory.API.MathTools.Vec3i(100, 4, 100);
            // walking/flying: border crossings move the center one chunk at a time
            if (FastChunkCuller.IsTeleportJump(c, 101, 4, 100)) throw new Exception("one-chunk step counted as teleport");
            if (FastChunkCuller.IsTeleportJump(c, 107, 4, 107)) throw new Exception("7 chunks diagonal counted as teleport");
            if (!FastChunkCuller.IsTeleportJump(c, 108, 4, 100)) throw new Exception("8-chunk jump not a teleport");
            if (!FastChunkCuller.IsTeleportJump(c, 100, 4, 400)) throw new Exception("far teleport not detected");
            if (!FastChunkCuller.IsTeleportJump(null, 0, 0, 0)) throw new Exception("first pass must count as a jump");

            // inside the burst window the limit stands down; after it, it applies again -
            // and it never applies to border-crossing passes, burst or not
            if (FastChunkCuller.RateLimitApplies(samePosition: true, 200, now: 1000, burstUntil: 2000))
                throw new Exception("rate limit applied inside the burst window");
            if (!FastChunkCuller.RateLimitApplies(samePosition: true, 200, now: 2000, burstUntil: 2000))
                throw new Exception("rate limit missing after the burst window");
            if (FastChunkCuller.RateLimitApplies(samePosition: false, 200, now: 5000, burstUntil: 0))
                throw new Exception("rate limit applied to a border-crossing pass");
            if (FastChunkCuller.RateLimitApplies(samePosition: true, 0, now: 5000, burstUntil: 0))
                throw new Exception("rate limit applied with the interval configured off");
        });

        Check("firepit gate rule and patch", () =>
        {
            // the rule: only the mesh-only path may be skipped, and only beyond the limit
            if (FirepitPatches.ShouldSkip(hasChildRenderer: false, distSq: 65 * 65, maxDistance: 64) != true)
                throw new Exception("distant mesh-only firepit not skipped");
            if (FirepitPatches.ShouldSkip(hasChildRenderer: false, distSq: 63 * 63, maxDistance: 64) != false)
                throw new Exception("near firepit skipped");
            if (FirepitPatches.ShouldSkip(hasChildRenderer: true, distSq: 500 * 500, maxDistance: 64) != false)
                throw new Exception("a pot renderer manages the cooking sound and must never be gated");
            if (FirepitPatches.ShouldSkip(hasChildRenderer: false, distSq: 500 * 500, maxDistance: 0) != false)
                throw new Exception("maxDistance 0 must mean vanilla");

            // and the patch really lands on the survival mod's renderer
            string install = Environment.GetEnvironmentVariable("VS_INSTALL") ?? "/opt/vintagestory";
            System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(install, "Mods", "VSSurvivalMod.dll"));
            FirepitPatches.Apply(harmony, 64, 150);
            ForceJit(AccessTools.Method(
                AccessTools.TypeByName("Vintagestory.GameContent.FirepitContentsRenderer"), "OnRenderFrame"));
            FirepitPatches.Enabled = false; // no game in this process
        });

        Check("entity tesselation budget: rule, patch, and the tick-thread gate", () =>
        {
            // The rule: the first tesselation of a frame always runs (liveness), further ones
            // only while the budget is not spent. Counter-checked once by removing the
            // liveness clause: the zero-budget case then starves.
            if (!EntityTessPatches.ShouldTesselate(spentMs: 0, allowedCount: 0, budgetMs: 2))
                throw new Exception("first tesselation of a frame was refused");
            if (!EntityTessPatches.ShouldTesselate(spentMs: 0, allowedCount: 0, budgetMs: 0))
                throw new Exception("liveness: even at budget 0 the first call must run");
            if (!EntityTessPatches.ShouldTesselate(spentMs: 1.5, allowedCount: 3, budgetMs: 2))
                throw new Exception("refused although the budget is not spent");
            if (EntityTessPatches.ShouldTesselate(spentMs: 2.1, allowedCount: 1, budgetMs: 2))
                throw new Exception("allowed although the budget is spent");

            // the patch really lands on VSEssentials' renderer, and only on the base method -
            // EntityPlayerShapeRenderer overrides TesselateShape() without calling base, which
            // is what keeps the player out of the budget by construction
            string install = Environment.GetEnvironmentVariable("VS_INSTALL") ?? "/opt/vintagestory";
            System.Reflection.Assembly.LoadFrom(System.IO.Path.Combine(install, "Mods", "VSEssentials.dll"));
            EntityTessPatches.Apply(harmony, 2.0);
            System.Reflection.MethodInfo tessM = AccessTools.Method(
                AccessTools.TypeByName("Vintagestory.GameContent.EntityShapeRenderer"), "TesselateShape", Type.EmptyTypes);
            ForceJit(tessM);
            System.Reflection.MethodBase playerTess = AccessTools.Method(
                AccessTools.TypeByName("Vintagestory.GameContent.EntityPlayerShapeRenderer"), "TesselateShape", Type.EmptyTypes);
            if (playerTess == null || playerTess == tessM)
                throw new Exception("EntityPlayerShapeRenderer no longer has its own TesselateShape - the player would be budgeted too");
            EntityTessPatches.Enabled = false; // no game in this process

            // The tick gate: TriggerGameTick's postfix fires on the SERVER thread too in
            // singleplayer (CoreServerEventManager calls base), and used to book server ticks
            // into the client frame - "tick 26,6 ms" inside a 26,2 ms frame in the first real
            // hitch log. A world that is not ClientMain must book nothing.
            FrameStats.Reset();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency; // 1 s ago
            MeasurementPatches.TickPostfix(t0, world: null);
            long before = System.Diagnostics.Stopwatch.GetTimestamp();
            FrameStats.Advance(before, 0);
            FrameStats.Advance(before + System.Diagnostics.Stopwatch.Frequency / 100, 0); // a 10 ms frame
            if (FrameStats.GameTickMs > 0.01)
                throw new Exception($"a non-client world booked {FrameStats.GameTickMs:F1} ms of game tick");
            FrameStats.Reset();
        });

        Check("shadow map resolution steps land on both cascades", () =>
        {
            // The transpiler must find exactly the two size expressions and nothing else -
            // resizing the wrong framebuffer would be invisible until something rendered
            // wrong. Counter-checked by requiring three matches: the patch then throws.
            ShadowResPatches.Apply(harmony, extraSteps: 1);
            ForceJit(AccessTools.Method(typeof(Vintagestory.Client.NoObf.ClientPlatformWindows),
                "SetupDefaultFrameBuffers"));

            // the spliced helper turns the engine's steps into the allocated steps
            ShadowResPatches.ExtraSteps = 2;
            if (ShadowResPatches.AddSteps(6) != 8) throw new Exception("extra steps not applied");
            if (ShadowResPatches.ShadowMapSize != 8192)
                throw new Exception($"map size {ShadowResPatches.ShadowMapSize}, expected 8192");
            ShadowResPatches.ExtraSteps = 0;
            if (ShadowResPatches.AddSteps(6) != 6) throw new Exception("zero steps must be vanilla");

            // Whatever the framebuffer really ended up as is what the texel grid has to be
            // quantised to. Snapping used the settings formula alone until 1.40.0, so with the
            // resolution patch active it quantised to a 6144 grid on a 7168 map - a grid the
            // sampler does not have, which is not a snap at all.
            if (ShadowResPatches.EffectiveMapSize != ShadowResPatches.ShadowMapSize)
                throw new Exception($"effective size {ShadowResPatches.EffectiveMapSize} ignores the "
                                    + $"real framebuffer {ShadowResPatches.ShadowMapSize}");
        });

        Check("prebuilt window is rejected when its neighbourhood was marked dirty", () =>
        {
            // The defect this closes: the old guard compared chunk.Data by REFERENCE, which
            // in-place mutations (block edit, server light update, arriving neighbour) never
            // change - so stale windows passed and, once the initial validation run ended,
            // silently produced chunk meshes with wrong blocks and wrong light. The dirty
            // mark is the exact signal for all of those.
            ChunkMarkClock.Clear();
            ChunkMarkClock.Enabled = true;

            const int mulX = 4096, mulZ = 4096;
            long[] keys = new long[27];
            int k = 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        keys[k++] = ChunkMarkClock.Key(100 + dx, 5 + dy, 200 + dz, mulX, mulZ);

            long buildStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!WindowPrebuilder.NeighbourhoodUnchanged(keys, buildStart))
                throw new Exception("an untouched neighbourhood was rejected");

            // a mark on a CORNER neighbour after the build must invalidate the window
            System.Threading.Thread.Sleep(2);
            ChunkMarkClock.Note(ChunkMarkClock.Key(99, 4, 199, mulX, mulZ));
            if (WindowPrebuilder.NeighbourhoodUnchanged(keys, buildStart))
                throw new Exception("a dirty corner neighbour did not invalidate the window");

            // a mark on a chunk OUTSIDE the neighbourhood must not
            ChunkMarkClock.Clear();
            ChunkMarkClock.Note(ChunkMarkClock.Key(103, 5, 200, mulX, mulZ));
            if (!WindowPrebuilder.NeighbourhoodUnchanged(keys, buildStart))
                throw new Exception("an unrelated chunk invalidated the window");

            // a mark that happened BEFORE the build started is not a reason to reject
            ChunkMarkClock.Clear();
            ChunkMarkClock.Note(keys[13]);
            long laterBuild = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency;
            if (!WindowPrebuilder.NeighbourhoodUnchanged(keys, laterBuild))
                throw new Exception("a mark older than the build invalidated the window");

            // the key formula must match the engine's: ((y * mulZ) + z) * mulX + x
            if (ChunkMarkClock.Key(7, 3, 11, mulX, mulZ) != ((3L * mulZ) + 11) * mulX + 7)
                throw new Exception("chunk key formula does not match the engine's");

            ChunkMarkClock.Clear();
            ChunkMarkClock.Enabled = false;
        });

        Check("dirty-mark source sampling: patch and frame picking", () =>
        {
            RetessSourcePatches.Apply(harmony);
            Type mapType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientWorldMap");
            ForceJit(AccessTools.Method(mapType, "SetChunkDirty"));
            ForceJit(AccessTools.Method(mapType, "MarkChunkDirty"));

            // The picking rule: skip the marking plumbing, patch machinery and synthetic
            // frames; the first real caller wins. Counter-checked once by removing the
            // plumbing skip - the first case then returns the plumbing frame.
            var frames = new List<(string, string)>
            {
                (null, "DMD<something>"),                       // Harmony dynamic method
                ("ClientWorldMap", "SetChunkDirty"),
                ("ClientWorldMap", "MarkChunkDirty"),
                ("RetessSourcePatches", "Note"),
                ("ClientSystemRelight", "OnServerLightLevels"),
                ("ClientMain", "Process"),
            };
            string picked = RetessSourcePatches.PickSource(frames);
            if (picked != "ClientSystemRelight.OnServerLightLevels")
                throw new Exception($"picked '{picked}' instead of the first real caller");
            if (RetessSourcePatches.PickSource(new List<(string, string)>()) != null)
                throw new Exception("empty stack must pick nothing");

            // The capture rate cap: sampling always runs, but a mark storm (measured 7244/s
            // while streaming) must never buy more than MaxCapturesPerSecond stack walks -
            // and the bucket must refill, or a session-long ranking starves after a second.
            RetessSourcePatches.Reset();
            long t0 = System.Diagnostics.Stopwatch.Frequency * 100;
            int allowed = 0;
            for (int i = 0; i < 200; i++)
                if (RetessSourcePatches.BucketAllows(t0)) allowed++;
            if (allowed != RetessSourcePatches.MaxCapturesPerSecond)
                throw new Exception($"cap not enforced: {allowed} captures in one second");
            if (!RetessSourcePatches.BucketAllows(t0 + System.Diagnostics.Stopwatch.Frequency))
                throw new Exception("the bucket did not refill after a second");

            RetessSourcePatches.Reset();
            RetessSourcePatches.Enabled = false; // no game in this process
        });

        Check("edge coalescing: six marks become one flush, nothing strands", () =>
        {
            EdgeCoalescePatches.Apply(harmony, 400);
            ForceJit(AccessTools.Method(
                AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientWorldMap"), "MarkChunkDirty"));

            // the pure center: a chunk marked six times inside the window is due exactly once,
            // and the deadline is fixed by the FIRST mark - constant re-marking must never
            // push it out (counter-checked once by making Note refresh the deadline: the
            // "due despite re-mark" assert fails)
            var c = new EdgeCoalescePatches.Coalescer();
            long tick0 = 1_000_000, window = 400;
            long key = EdgeCoalescePatches.Pack(12, 3, 7);
            int fresh = 0;
            for (int i = 0; i < 6; i++)
                if (c.Note(key, tick0 + i * 10, window)) fresh++;
            if (fresh != 1) throw new Exception($"{fresh} pending entries from six marks of one chunk");
            if (c.Note(EdgeCoalescePatches.Pack(13, 3, 7), tick0, window) != true)
                throw new Exception("a different chunk was absorbed");

            var due = new List<long>();
            c.CollectDue(tick0 + 399, due);
            if (due.Count != 0) throw new Exception("flushed before the window closed");
            c.Note(key, tick0 + 390, window); // re-mark just before the deadline
            c.CollectDue(tick0 + window, due);
            if (!due.Contains(key)) throw new Exception("due despite re-mark - deadline must be fixed, not sliding");
            if (due.Count != 2) throw new Exception($"{due.Count} due, expected both chunks");
            due.Clear();
            c.CollectDue(tick0 + 10 * window, due);
            if (due.Count != 0) throw new Exception("a flushed chunk was collected twice");

            // FlushAll drains regardless of deadline - the disable/safemode path
            c.Note(key, tick0, window);
            due.Clear();
            c.CollectAll(due);
            if (due.Count != 1 || c.Count != 0) throw new Exception("FlushAll left marks behind");

            // the per-tick cap: a flood of due marks drains in slices, and nothing is lost
            for (int i = 0; i < 300; i++) c.Note(EdgeCoalescePatches.Pack(i, 1, 1), tick0, window);
            due.Clear();
            c.CollectDue(tick0 + 2 * window, due, max: 192);
            if (due.Count != 192) throw new Exception($"cap collected {due.Count}, expected 192");
            if (c.Count != 108) throw new Exception($"{c.Count} left pending, expected 108");
            due.Clear();
            c.CollectDue(tick0 + 2 * window, due, max: 192);
            if (due.Count != 108 || c.Count != 0) throw new Exception("second slice did not drain the rest");

            // The catch-up rule, born from a shipped artifact: a flat cap below the flood's
            // inflow let 32 900 held marks pile up - visible holes along water chunk borders.
            // Above the threshold the cap scales with the backlog, so the backlog decays
            // (x0,75 per tick) no matter how fast marks arrive. Counter-check: with the
            // flat 1.35.2 cap the drain-beats-inflow assert below fails.
            if (EdgeCoalescePatches.CapFor(100) != EdgeCoalescePatches.MaxFlushPerTick)
                throw new Exception("small backlog must use the polite cap");
            if (EdgeCoalescePatches.CapFor(32900) != 8225)
                throw new Exception($"catch-up cap {EdgeCoalescePatches.CapFor(32900)}, expected a quarter of the backlog");
            int backlog = 32900;
            for (int tick = 0; tick < 20 && backlog > 0; tick++)
                backlog = Math.Max(0, backlog - EdgeCoalescePatches.CapFor(backlog)) + 60; // 60 new marks per 50 ms ~ flood inflow
            if (backlog > 2000)
                throw new Exception($"backlog still {backlog} after one second of catch-up - drain does not beat inflow");

            // packing survives the round trip at real map coordinates
            (int ux, int uy, int uz) = EdgeCoalescePatches.Unpack(EdgeCoalescePatches.Pack(15000, 2049, 15000));
            if (ux != 15000 || uy != 2049 || uz != 15000) throw new Exception("coordinate packing lost bits");

            EdgeCoalescePatches.Reset();
            EdgeCoalescePatches.Enabled = false; // no game in this process
        });

        Check("edge retess priority: visible border repairs jump the queue, nothing is lost", () =>
        {
            EdgeRetessPriorityPatches.Apply(harmony);
            ForceJit(AccessTools.Method(
                AccessTools.TypeByName("Vintagestory.Client.NoObf.ChunkTesselatorManager"),
                "OnSeperateThreadGameTick"));

            // the real key encoding: sign bit = edge-only, exactly as the producers set it
            static long Neg(long k) => k | long.MinValue;
            static string Render(UniqueQueue<long> q)
            {
                var parts = new List<string>();
                foreach (long k in q) parts.Add(k < 0 ? "e" + (k & long.MaxValue) : k.ToString());
                return string.Join(",", parts);
            }
            static void ExpectOrder(UniqueQueue<long> q, string expected, string what)
            {
                if (Render(q) != expected)
                    throw new Exception($"{what}: [{Render(q)}], expected [{expected}]");
            }

            var dirty = new UniqueQueue<long>();
            var prio = new UniqueQueue<long>();
            object dl = new object(), pl = new object();

            // the shape the flood produces: full entries (positive) interleaved with edge
            // repairs (negative), FIFO. Cap 2 takes the two oldest repairs, in order, and
            // leaves everything else exactly where it was.
            // (Counter-checked once by inverting the selection to k >= 0: the order assert
            // below fails, and the conservation fuzz fails with it.)
            dirty.Enqueue(10); dirty.Enqueue(Neg(11)); dirty.Enqueue(12);
            dirty.Enqueue(Neg(13)); dirty.Enqueue(14); dirty.Enqueue(Neg(15));

            int moved = EdgeRetessPriorityPatches.Promote(dirty, dl, prio, pl, cap: 2);
            if (moved != 2) throw new Exception($"moved {moved}, expected the cap of 2");
            ExpectOrder(prio, "e11,e13", "promoted keys wrong or out of order");
            ExpectOrder(dirty, "10,12,14,e15", "rotation scrambled the keepers");

            // the capped-out repair is not stranded: the next sweep takes it.
            // (Counter-checked once by dropping the re-enqueue of over-cap edge keys in the
            // rotation: this assert fails - the key had silently vanished.)
            while (prio.Count > 0) prio.Dequeue();
            moved = EdgeRetessPriorityPatches.Promote(dirty, dl, prio, pl, cap: 64);
            if (moved != 1 || prio.Dequeue() != Neg(15))
                throw new Exception("the capped-out edge key was not promoted on the next sweep");
            ExpectOrder(dirty, "10,12,14", "a full entry went missing");

            // a repair that is already urgent merges instead of doubling (UniqueQueue dedup);
            // it still counts as moved, because it did leave the normal queue
            dirty.Enqueue(Neg(20)); prio.Enqueue(Neg(20));
            moved = EdgeRetessPriorityPatches.Promote(dirty, dl, prio, pl, cap: 64);
            if (moved != 1) throw new Exception("dedup swallowed the move count");
            if (prio.Count != 1) throw new Exception("a duplicate repair entry was created");
            prio.Dequeue();

            // no edge keys -> the sweep is a no-op and must not reorder anything
            moved = EdgeRetessPriorityPatches.Promote(dirty, dl, prio, pl, cap: 64);
            if (moved != 0 || prio.Count != 0)
                throw new Exception("promoted from a queue with no edge keys");
            ExpectOrder(dirty, "10,12,14", "a no-op sweep reordered the queue");

            // conservation under fire: floods in, capped sweeps and consumer drains
            // interleaved - every key sits in exactly one queue until consumed, none
            // invented, none lost. This is the fuzz that would have caught the two
            // capacity-divergence crashes of the culling cache, so the queue surgery gets
            // one from day one.
            var rnd = new Random(42);
            var expect = new HashSet<long>();
            foreach (long k in dirty) expect.Add(k);
            long next = 100;
            for (int round = 0; round < 400; round++)
            {
                int add = rnd.Next(0, 8);
                for (int i = 0; i < add; i++)
                {
                    long k = next++;
                    if (rnd.Next(2) == 0) k = Neg(k);
                    dirty.Enqueue(k); expect.Add(k);
                }
                if (rnd.Next(3) == 0)
                    EdgeRetessPriorityPatches.Promote(dirty, dl, prio, pl, rnd.Next(0, 5));
                int eat = rnd.Next(0, 4);
                for (int i = 0; i < eat && prio.Count > 0; i++) expect.Remove(prio.Dequeue());
                for (int i = 0; i < eat && dirty.Count > 0; i++) expect.Remove(dirty.Dequeue());
            }
            var leftOver = new HashSet<long>();
            foreach (long k in dirty) leftOver.Add(k);
            foreach (long k in prio) leftOver.Add(k);
            if (!leftOver.SetEquals(expect))
                throw new Exception($"conservation broken: {leftOver.Count} keys in the queues, expected {expect.Count}");

            // the capacity lesson, encoded like the coalescer's catch-up rule: promotions
            // per second must beat the measured flood inflow of ~1150 edge marks/s, or the
            // visible backlog merely splits in two
            if (EdgeRetessPriorityPatches.MaxPromotedPerSweep
                * (1000 / EdgeRetessPriorityPatches.SweepIntervalMs) < 1200)
                throw new Exception("promotion capacity below flood inflow");

            EdgeRetessPriorityPatches.Reset();
            EdgeRetessPriorityPatches.Enabled = false; // no game in this process
        });

        Check("pool reclaimer refuses anything that still holds geometry", () =>
        {
            PoolReclaimer.EnsureReady();
            const double after = 20.0;

            // the one rule that must never bend: a pool with locations is untouchable, no
            // matter how long ago it was last seen empty
            if (PoolReclaimer.ShouldReclaim(1, 500000, 0, 0.0, 10_000.0, after))
                throw new Exception("would have reclaimed a pool that still holds geometry");
            if (PoolReclaimer.ShouldReclaim(3000, 500000, 0, 0.0, 10_000.0, after))
                throw new Exception("would have reclaimed a full pool");

            // already reclaimed, mini dimension, and "first seen empty just now"
            if (PoolReclaimer.ShouldReclaim(0, 0, 0, 0.0, 10_000.0, after))
                throw new Exception("reclaimed an already reclaimed pool twice");
            if (PoolReclaimer.ShouldReclaim(0, 500000, 1, 0.0, 10_000.0, after))
                throw new Exception("touched a mini dimension pool");
            if (PoolReclaimer.ShouldReclaim(0, 500000, 0, -1, 10_000.0, after))
                throw new Exception("reclaimed before the clock had even started");

            // and the window itself
            if (PoolReclaimer.ShouldReclaim(0, 500000, 0, 100.0, 100.0 + after - 0.01, after))
                throw new Exception("reclaimed before the window elapsed");
            if (!PoolReclaimer.ShouldReclaim(0, 500000, 0, 100.0, 100.0 + after, after))
                throw new Exception("did not reclaim after the window elapsed");
        });

        Check("a reclaimed pool refuses new geometry instead of crashing", () =>
        {
            // Zero capacity is the whole mechanism: TryAppend must return null so
            // MeshDataPoolManager.AddModel moves on, rather than writing into buffers that
            // have been deleted.
            MeshDataPool pool = NewPool();
            var locations = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(pool);
            locations.Clear();
            FastCuller.Invalidate(pool);
            pool.VerticesPoolSize = 0;
            pool.IndicesPoolSize = 0;
            pool.indicesStartsByte = Array.Empty<int>();
            pool.indicesSizes = Array.Empty<int>();

            // culling an emptied pool must produce zero draw ranges and must not index the
            // now-empty scratch arrays
            pool.FrustumCull(NewCuller(), EnumFrustumCullMode.CullNormal);
            if (pool.indicesGroupsCount != 0) throw new Exception($"{pool.indicesGroupsCount} ranges from an empty pool");
            pool.FrustumCull(NewCuller(), EnumFrustumCullMode.NoCull);
            if (pool.indicesGroupsCount != 0) throw new Exception("NoCull drew from an empty pool");
        });

        Check("renderer wrapping forwards everything and times it", () =>
        {
            // Wrapping every renderer means every renderer's contract has to be forwarded -
            // a wrapper that got RenderOrder wrong would silently reorder the whole frame.
            var order = new List<string>();
            var manager = (Vintagestory.Client.NoObf.ClientEventManager)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(Vintagestory.Client.NoObf.ClientEventManager));
            int stages = Enum.GetValues(typeof(EnumRenderStage)).Length;
            manager.renderersByStage = new List<Vintagestory.Client.NoObf.RenderHandler>[stages];
            for (int i = 0; i < stages; i++) manager.renderersByStage[i] = new List<Vintagestory.Client.NoObf.RenderHandler>();

            var originals = new List<RecordingRenderer>();
            for (int i = 0; i < 4; i++)
            {
                var r = new RecordingRenderer(order, "renderer" + i, 0.1 * i, i);
                originals.Add(r);
                manager.renderersByStage[(int)EnumRenderStage.Opaque].Add(
                    new Vintagestory.Client.NoObf.RenderHandler { ProfilingName = "renderer" + i, Renderer = r });
            }

            RendererProfiler.Reset();

            // Off by default since 1.42.0, and "off" has to mean nothing is wrapped at all -
            // not "wrapped but not timing". Ten thousand decorators still cost an interface
            // dispatch each, which is exactly the cost the default exists to avoid.
            RendererProfiler.Enabled = false;
            RendererProfiler.Wrap(manager);
            if (RendererProfiler.StatWrapped != 0)
                throw new Exception("wrapped renderers while the profiler was switched off");

            RendererProfiler.Enabled = true;
            RendererProfiler.Wrap(manager);

            var list = manager.renderersByStage[(int)EnumRenderStage.Opaque];
            for (int i = 0; i < 4; i++)
            {
                if (ReferenceEquals(list[i].Renderer, originals[i])) throw new Exception($"renderer {i} was not wrapped");
                if (list[i].Renderer.RenderOrder != originals[i].RenderOrder)
                    throw new Exception($"renderer {i}: RenderOrder not forwarded");
                if (list[i].Renderer.RenderRange != originals[i].RenderRange)
                    throw new Exception($"renderer {i}: RenderRange not forwarded");
            }

            // the instance counts have to be the truth, not the name count - hundreds of
            // firepits share one profiling name, so "94 renderer" was misleading
            if (RendererProfiler.StatTotal != 4)
                throw new Exception($"StatTotal {RendererProfiler.StatTotal}, expected 4 instances");
            if (RendererProfiler.StatWrapped != 4)
                throw new Exception($"StatWrapped {RendererProfiler.StatWrapped}, expected 4");

            // wrapping twice must not stack decorators
            RendererProfiler.Wrap(manager);
            if (RendererProfiler.StatWrapped != 4 || RendererProfiler.StatTotal != 4)
                throw new Exception("counts drifted on a second wrap pass");
            if (list[0].Renderer is RendererProfiler.Timed outer && outer.Inner is RendererProfiler.Timed)
                throw new Exception("wrapped a wrapper");

            // The wrapper has to call through, in order, on every frame. Only every fourth
            // frame is *measured*, so this runs whole frames rather than one burst - a burst
            // could land entirely on unmeasured frames and report nothing.
            const int frames = 400, passes = 600;
            for (int frame = 0; frame < frames; frame++)
            {
                for (int i = 0; i < passes; i++)
                    for (int r = 0; r < list.Count; r++)
                        list[r].Renderer.OnRenderFrame(0.016f, EnumRenderStage.Opaque);
                RendererProfiler.EndFrame();
            }
            if (order.Count == 0) throw new Exception("the wrapper never called the real renderer");
            for (int i = 0; i < 4; i++)
                if (order[i] != "renderer" + i) throw new Exception($"position {i} ran '{order[i]}'");

            // Sampling must skip the measurement, never the call itself.
            if (order.Count != frames * passes * list.Count)
                throw new Exception($"{order.Count} dispatches of {frames * passes * list.Count} - sampling swallowed calls");

            var top = RendererProfiler.Top(5);
            if (top.Count != 4) throw new Exception($"{top.Count} renderers timed, expected 4");
            foreach ((string name, EnumRenderStage st, double ms) in top)
            {
                if (st != EnumRenderStage.Opaque) throw new Exception($"{name} attributed to {st}");
                if (ms <= 0) throw new Exception($"{name} reported {ms} ms");
            }

            // and unwrapping has to leave no trace
            RendererProfiler.Unwrap(manager);
            for (int i = 0; i < 4; i++)
                if (!ReferenceEquals(list[i].Renderer, originals[i]))
                    throw new Exception($"renderer {i} was not restored");
            // StatWrapped is what keeps the unregister fix scanning; leaving it non-zero after
            // an unwrap would mean paying for that scan forever with nothing left to find
            if (RendererProfiler.StatWrapped != 0)
                throw new Exception($"StatWrapped {RendererProfiler.StatWrapped} after unwrapping everything");
            if (RendererProfiler.Enabled)
                throw new Exception("Unwrap left the profiler marked enabled");

            RendererProfiler.Reset();
        });

        Check("the before stage stays attributed while the profiler is off", () =>
        {
            // The world-join bursts (60-87 ms of "before" with no GC and no renderer name)
            // stayed unnamed for weeks because naming them required arming the full profiler
            // before joining. The Before stage holds only a handful of system renderers, so
            // those are now wrapped and timed EVERY frame even with the profiler off - and a
            // hitch on any frame, sampled or not, can name its Before renderer.
            var manager = (Vintagestory.Client.NoObf.ClientEventManager)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(Vintagestory.Client.NoObf.ClientEventManager));
            int stages = Enum.GetValues(typeof(EnumRenderStage)).Length;
            manager.renderersByStage = new List<Vintagestory.Client.NoObf.RenderHandler>[stages];
            for (int i = 0; i < stages; i++) manager.renderersByStage[i] = new List<Vintagestory.Client.NoObf.RenderHandler>();

            var beforeRenderer = new SpinningRenderer();
            var opaqueRenderer = new SpinningRenderer();
            var beforeList = manager.renderersByStage[(int)EnumRenderStage.Before];
            var opaqueList = manager.renderersByStage[(int)EnumRenderStage.Opaque];
            beforeList.Add(new Vintagestory.Client.NoObf.RenderHandler { ProfilingName = "Before-probe", Renderer = beforeRenderer });
            opaqueList.Add(new Vintagestory.Client.NoObf.RenderHandler { ProfilingName = "Opaque-other", Renderer = opaqueRenderer });

            RendererProfiler.Reset();
            RendererProfiler.Enabled = false;
            RendererProfiler.AttributeBeforeStage = true;
            RendererProfiler.Wrap(manager);

            if (beforeList[0].Renderer is not RendererProfiler.Timed)
                throw new Exception("the before renderer was not wrapped with the profiler off");
            if (opaqueList[0].Renderer is RendererProfiler.Timed)
                throw new Exception("an opaque renderer was wrapped with the profiler off - that is the 10 000-decorator cost the default avoids");

            // Every frame must be readable at the boundary, not just the sampled quarter -
            // that is the whole point: a hitch does not wait to be sampled.
            for (int frame = 0; frame < 8; frame++)
            {
                beforeList[0].Renderer.OnRenderFrame(0.016f, EnumRenderStage.Before);
                (string name, double ms)? top = RendererProfiler.TopOfCurrentFrame();
                if (top == null || top.Value.name != "Before-probe" || top.Value.ms <= 0)
                    throw new Exception($"frame {frame}: before renderer not readable at the boundary (got {top?.name ?? "null"})");
                RendererProfiler.EndFrame();
            }

            // Toggling the full profiler on and off again must keep the attribution armed.
            RendererProfiler.Enabled = true;
            RendererProfiler.Wrap(manager);
            if (opaqueList[0].Renderer is not RendererProfiler.Timed)
                throw new Exception("full profiler did not wrap the opaque renderer");
            RendererProfiler.Unwrap(manager, keepBeforeAttribution: true);
            if (RendererProfiler.Enabled) throw new Exception("Unwrap left the profiler marked enabled");
            if (beforeList[0].Renderer is not RendererProfiler.Timed)
                throw new Exception("profiler toggle-off must keep the before attribution");
            if (!ReferenceEquals(opaqueList[0].Renderer, opaqueRenderer))
                throw new Exception("profiler toggle-off did not restore the opaque renderer");
            if (RendererProfiler.StatWrapped == 0)
                throw new Exception("StatWrapped 0 with a decorator still installed - the unregister fix would disarm");

            // The full teardown (world leave) takes everything out.
            RendererProfiler.Unwrap(manager);
            if (beforeList[0].Renderer is RendererProfiler.Timed)
                throw new Exception("full unwrap left the before wrapper in place");
            if (RendererProfiler.StatWrapped != 0)
                throw new Exception("StatWrapped after full unwrap");

            RendererProfiler.Reset();
            RendererProfiler.AttributeBeforeStage = true;
        });

        Check("sun query throttle skips only when it can restore the GL state", () =>
        {
            SunQueryPatches.Apply(harmony, 4);
            MethodInfo post = AccessTools.Method(typeof(Vintagestory.Client.NoObf.SystemRenderSunMoon),
                "OnRenderFrame3DPost", new[] { typeof(float) });
            if (post == null) throw new Exception("OnRenderFrame3DPost not found");
            ForceJit(post);

            // The pass runs dead last in the opaque stage, and the OIT stage - sky and clouds -
            // inherits the GL state it leaves behind. Skipping it without restoring that state
            // made the sky flicker on a four-frame beat. So the contract is now: WITH a
            // platform, skip three of four frames (and restore state); WITHOUT one, never skip,
            // because a skip whose state cannot be restored is the bug this test pins down.
            SunQueryPatches.ResetForTests();
            int ran = 0;
            for (int frame = 0; frame < 400; frame++)
                if (SunQueryPatches.ThrottleQuery(null)) ran++;

            if (ran != 400)
                throw new Exception($"skipped {400 - ran} frames with no platform to restore the state");
            if (SunQueryPatches.StatSkipped != 0)
                throw new Exception("counted skips that must not have happened");

            // interval 1 stays exactly vanilla
            SunQueryPatches.ResetForTests();
            SunQueryPatches.Interval = 1;
            ran = 0;
            for (int frame = 0; frame < 50; frame++) if (SunQueryPatches.ThrottleQuery(null)) ran++;
            if (ran != 50) throw new Exception($"interval 1 skipped {50 - ran} frames");

            SunQueryPatches.Interval = 4;
            SunQueryPatches.ResetForTests();
        });

        Check("shadow texel snapping quantises and never moves more than a texel", () =>
        {
            ShadowStabilityPatches.Apply(harmony);
            MethodInfo ortho = AccessTools.Method(typeof(Vintagestory.Client.NoObf.SystemRenderShadowMap),
                "loadOrthoModeMatrix", new[] { typeof(double[]), typeof(double), typeof(double), typeof(double) });
            ForceJit(ortho);

            // Identity light view, so light space equals world space and the arithmetic can be
            // checked directly. The property that matters: whatever the camera position, the
            // projection lands on a fixed grid and never shifts by more than one texel.
            double[] identity = { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
            const double width = 400, height = 300;
            const int mapSize = 6144;                    // shadow quality 4
            double texelX = width / mapSize;             // 0.0651 blocks

            var offsets = new List<double>();
            for (int i = 0; i < 400; i++)
            {
                double x = i * texelX / 20.0;            // twenty samples per texel
                if (!ShadowStabilityPatches.SnapOffset(identity, x, 64, 0, width, height, mapSize,
                                                       out double ox, out double oy))
                    throw new Exception("declined a perfectly valid light matrix");

                if (ox < -1e-9 || ox > texelX + 1e-9)
                    throw new Exception($"offset {ox} outside [0, {texelX}] - the map would jump");

                // the snapped position has to sit on a whole texel
                double snapped = x - ox;
                double rem = Math.Abs(snapped / texelX - Math.Round(snapped / texelX));
                if (rem > 1e-6) throw new Exception($"snapped position {snapped} is off the texel grid");
                offsets.Add(ox);
            }

            // and it must genuinely quantise: a projection that just followed the camera would
            // produce 400 distinct offsets instead of the ~20 samples-per-texel pattern
            offsets.Sort();
            int distinct = 1;
            for (int i = 1; i < offsets.Count; i++) if (offsets[i] - offsets[i - 1] > 1e-9) distinct++;
            if (distinct > 25) throw new Exception($"{distinct} distinct offsets - this is not quantising");

            // a rotated light matrix must still snap, just on its own axes
            double a = 0.7;
            double[] rotated = { Math.Cos(a),0,Math.Sin(a),0, 0,1,0,0, -Math.Sin(a),0,Math.Cos(a),0, 0,0,0,1 };
            if (!ShadowStabilityPatches.SnapOffset(rotated, 123.456, 64, -77.7, width, height, mapSize,
                                                   out double rx, out double ry))
                throw new Exception("declined a rotated light matrix");
            if (rx < 0 || rx > texelX || ry < 0 || ry > height / mapSize)
                throw new Exception("rotated light matrix produced an out-of-range offset");

            // and nonsense input must be declined, not crash
            if (ShadowStabilityPatches.SnapOffset(null, 0, 0, 0, width, height, mapSize, out _, out _))
                throw new Exception("accepted a null light matrix");
            if (ShadowStabilityPatches.SnapOffset(identity, 0, 0, 0, width, height, 0, out _, out _))
                throw new Exception("accepted a zero-sized shadow map");

            ShadowStabilityPatches.Enabled = false;
        });

        Check("a wrapped renderer can still unregister itself", () =>
        {
            // The crash this pins down: UnregisterRenderer finds the entry by reference
            // (x.Renderer == handler). With the Timed wrapper in the list, a block entity
            // calling UnregisterRenderer(this) removed nothing, its ghost kept rendering the
            // meshes it had just disposed, and the game died with "Trying to render a
            // disposed mesh" in PotInFirepitRenderer.
            RendererProfiler.ApplyUnregisterFix(harmony);
            MethodInfo unreg = AccessTools.Method(typeof(Vintagestory.Client.NoObf.ClientEventManager),
                nameof(Vintagestory.Client.NoObf.ClientEventManager.UnregisterRenderer),
                new[] { typeof(IRenderer), typeof(EnumRenderStage) });
            ForceJit(unreg);

            var order = new List<string>();
            var manager = (Vintagestory.Client.NoObf.ClientEventManager)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(Vintagestory.Client.NoObf.ClientEventManager));
            int stages = Enum.GetValues(typeof(EnumRenderStage)).Length;
            manager.renderersByStage = new List<Vintagestory.Client.NoObf.RenderHandler>[stages];
            for (int i = 0; i < stages; i++) manager.renderersByStage[i] = new List<Vintagestory.Client.NoObf.RenderHandler>();

            var original = new RecordingRenderer(order, "victim");
            manager.renderersByStage[(int)EnumRenderStage.Opaque].Add(
                new Vintagestory.Client.NoObf.RenderHandler { ProfilingName = "victim", Renderer = original });

            RendererProfiler.Enabled = true;
            RendererProfiler.Wrap(manager);
            var list = manager.renderersByStage[(int)EnumRenderStage.Opaque];
            if (ReferenceEquals(list[0].Renderer, original)) throw new Exception("was not wrapped, test proves nothing");

            // the block entity's dispose path: unregister by the ORIGINAL reference
            manager.UnregisterRenderer(original, EnumRenderStage.Opaque);
            if (list.Count != 0)
                throw new Exception("unregistering the original left the wrapper registered - the ghost that crashed the game");

            // unregistering something that was never registered must stay a no-op
            manager.UnregisterRenderer(new RecordingRenderer(order, "stranger"), EnumRenderStage.Opaque);

            // and a renderer that was never wrapped must still unregister normally
            var plain = new RecordingRenderer(order, "plain");
            list.Add(new Vintagestory.Client.NoObf.RenderHandler { ProfilingName = "plain", Renderer = plain });
            manager.UnregisterRenderer(plain, EnumRenderStage.Opaque);
            if (list.Count != 0) throw new Exception("an unwrapped renderer could no longer unregister");

            // The window between "profiler switched off" and "everything unwrapped" is real:
            // the toggle does those as two steps, and a block entity unregistering in between
            // must still find its decorator. So the fix keys on wrappers existing, not on the
            // Enabled flag - check exactly that.
            var late = new RecordingRenderer(order, "late");
            list.Add(new Vintagestory.Client.NoObf.RenderHandler { ProfilingName = "late", Renderer = late });
            RendererProfiler.Wrap(manager);
            RendererProfiler.Enabled = false;               // toggled off, not yet unwrapped
            manager.UnregisterRenderer(late, EnumRenderStage.Opaque);
            if (list.Count != 0)
                throw new Exception("a renderer unregistering between 'off' and 'unwrapped' left a ghost");

            RendererProfiler.Reset();
        });

        Check("server tuning targets resolve", () =>
        {
            // The server mod system sets public statics on MagicNum and corrects a field on
            // the already-constructed ChunkServerThread. All of that is reflection-free
            // except ServerMain.chunkThread - resolve it here so an engine rename fails in
            // the harness, not silently at world start.
            var field = HarmonyLib.AccessTools.Field(typeof(Vintagestory.Server.ServerMain), "chunkThread");
            if (field == null) throw new Exception("ServerMain.chunkThread not found");
            if (field.FieldType != typeof(Vintagestory.Server.ChunkServerThread))
                throw new Exception($"chunkThread has unexpected type {field.FieldType.Name}");
            var count = HarmonyLib.AccessTools.Field(typeof(Vintagestory.Server.ChunkServerThread), "additionalWorldGenThreadsCount");
            if (count == null || count.FieldType != typeof(int))
                throw new Exception("additionalWorldGenThreadsCount not found or not int");
            // and the MagicNum statics the config drives
            _ = Vintagestory.Server.MagicNum.MaxWorldgenThreads;
            _ = Vintagestory.Server.MagicNum.RequestChunkColumnsQueueSize;
            _ = Vintagestory.Server.MagicNum.ChunksColumnsToRequestPerTick;
        });

        Check("window prebuild patches apply", () =>
        {
            WindowPipelinePatches.Apply(harmony, validateFirstN: 0);
            ForceJit(AccessTools.Method(typeof(Vintagestory.Client.NoObf.ChunkTesselator), "BuildExtendedChunkData"));
            WindowPrebuilder.Enabled = false; // no game in this process
        });

        Check("window fill plans match the first-principles cell mapping", () =>
        {
            // The plans transcribe vanilla's BuildExtendedChunkData index arithmetic. This
            // check derives the expected mapping independently - for every cell of the
            // 34^3 window, which neighbour chunk and which local index the block must come
            // from - and then simulates the plans against it. A transcription slip (an
            // off-by-one, a swapped axis, the wrong neighbour) shows up as a cell that is
            // unwritten, written twice, or written from the wrong place.
            const int dim = 34, cells = dim * dim * dim;

            (int chunk, int src) Expected(int wy, int wz, int wx)
            {
                int ly = wy - 1, lz = wz - 1, lx = wx - 1;
                int dy = ly < 0 ? 0 : ly > 31 ? 2 : 1;
                int dz = lz < 0 ? 0 : lz > 31 ? 2 : 1;
                int dx = lx < 0 ? 0 : lx > 31 ? 2 : 1;
                return (dx * 9 + dy * 3 + dz, ((ly & 31) * 32 + (lz & 31)) * 32 + (lx & 31));
            }

            void Simulate(bool skipCenter)
            {
                int[] written = new int[cells];
                var source = new (int chunk, int src)[cells];

                void Write(int cell, int chunk, int src)
                {
                    if ((uint)cell >= cells) throw new Exception($"write outside the window at {cell}");
                    written[cell]++;
                    source[cell] = (chunk, src);
                }

                int[] center = WindowPrebuilder.BuildCenterPlan(skipCenter);
                for (int p = 0; p < center.Length; p += 3)
                    for (int t = 0; t < center[p + 2] - center[p + 1]; t++)
                        Write(center[p] + 1 + t, 13, center[p + 1] + t);

                int[] border = WindowPrebuilder.BuildBorderPlan();
                for (int p = 0; p < border.Length; p += 4)
                {
                    if (border[p] == WindowPrebuilder.OpOne)
                        Write(border[p + 3], border[p + 1], border[p + 2]);
                    else
                        for (int t = 0; t < 32; t++)
                            Write(border[p + 3] + 1 + t, border[p + 1], border[p + 2] + t);
                }

                for (int wy = 0; wy < dim; wy++)
                for (int wz = 0; wz < dim; wz++)
                for (int wx = 0; wx < dim; wx++)
                {
                    int cell = (wy * dim + wz) * dim + wx;
                    int ly = wy - 1, lz = wz - 1, lx = wx - 1;
                    bool interior = ly is >= 0 and <= 31 && lz is >= 0 and <= 31 && lx is >= 0 and <= 31;
                    // in edge-only mode vanilla leaves deep-interior cells untouched; a row is
                    // filled when y or z is near a chunk face, otherwise only x 0,1,30,31
                    bool expectCovered = !skipCenter || !interior
                        || (ly + 2) % 32 <= 3 || (lz + 2) % 32 <= 3 || lx <= 1 || lx >= 30;

                    if (written[cell] != (expectCovered ? 1 : 0))
                        throw new Exception($"cell ({wy},{wz},{wx}) skip={skipCenter}: written {written[cell]}x, expected {(expectCovered ? 1 : 0)}");
                    if (!expectCovered) continue;

                    (int chunk, int src) want = Expected(wy, wz, wx);
                    if (source[cell] != want)
                        throw new Exception($"cell ({wy},{wz},{wx}) skip={skipCenter}: from chunk {source[cell].chunk} idx {source[cell].src}, "
                            + $"expected chunk {want.chunk} idx {want.src}");
                }
            }

            Simulate(skipCenter: false);
            Simulate(skipCenter: true);

            // the staleness rule, pinned: only a build younger than the last relight with
            // unchanged data objects may be used
            if (!WindowPrebuilder.WindowIsCurrent(builtAt: 100, lastRelightAt: 50, dataRefsMatch: true))
                throw new Exception("fresh window rejected");
            if (WindowPrebuilder.WindowIsCurrent(builtAt: 100, lastRelightAt: 150, dataRefsMatch: true))
                throw new Exception("relight after build must invalidate");
            if (WindowPrebuilder.WindowIsCurrent(builtAt: 100, lastRelightAt: 50, dataRefsMatch: false))
                throw new Exception("replaced chunk data must invalidate");
        });

        Check("tesselation patches apply", () =>
        {
            TesselationPatches.EnsureReady();
            TesselationPatches.Apply(harmony, noIdleSleep: true, raisePriority: false, prefetch: false);
            MethodInfo interval = AccessTools.Method(
                typeof(Vintagestory.Client.NoObf.ChunkTesselatorManager),
                nameof(Vintagestory.Client.NoObf.ChunkTesselatorManager.SeperateThreadTickIntervalMs));
            ForceJit(interval);
            MethodInfo tessTick = AccessTools.Method(
                typeof(Vintagestory.Client.NoObf.ChunkTesselatorManager), "OnSeperateThreadGameTick");
            ForceJit(tessTick);
        });

        Check("tesselation throughput accounting produces sane figures", () =>
        {
            TesselationStats.Reset();
            long msTicks = System.Diagnostics.Stopwatch.Frequency / 1000;
            TesselationStats.Sample(); // establish the baseline timestamp
            System.Threading.Thread.Sleep(120);
            for (int i = 0; i < 24; i++)
            {
                TesselationStats.AddChunk(msTicks * 4, edgeOnly: i % 4 == 0,   // 4 ms per chunk, a quarter edge-only
                    allocBytes: 4 * 1048576);                                  // 4 MB allocated per chunk
                TesselationStats.AddNeighbourTicks(msTicks, 3 * 1048576);      // 1 ms + 3 MB of that in neighbours
                TesselationStats.AddRelightTicks(msTicks / 2, 1048576 / 2);    // 0.5 ms + 0.5 MB relighting
                TesselationStats.AddPartsAlloc(1048576 / 4);                   // 0.25 MB in the part clones
                TesselationStats.AddJsonAlloc(1048576 / 8);                    // 0.125 MB in shape tesselation
            }
            TesselationStats.Sample();
            if (Math.Abs(TesselationStats.MsPerChunk - 4.0) > 0.5)
                throw new Exception($"ms/chunk {TesselationStats.MsPerChunk:F2}, expected ~4");
            if (Math.Abs(TesselationStats.NeighbourMsPerChunk - 1.0) > 0.2)
                throw new Exception($"neighbour ms {TesselationStats.NeighbourMsPerChunk:F2}, expected ~1");
            if (Math.Abs(TesselationStats.RelightMsPerChunk - 0.5) > 0.15)
                throw new Exception($"relight ms {TesselationStats.RelightMsPerChunk:F2}, expected ~0.5");
            if (Math.Abs(TesselationStats.EdgeSharePercent - 25.0) > 8)
                throw new Exception($"edge share {TesselationStats.EdgeSharePercent:F0}%, expected ~25");
            if (TesselationStats.ChunksPerSecond <= 0)
                throw new Exception("rate did not register");

            // the allocation split has to keep the phase proportions (3 : 0.5 of 4 MB per
            // chunk) - it is the figure that decides which phase gets opened up when the
            // recycler already reports 100% hits and the thread still allocates hundreds
            // of MB/s
            double total = TesselationStats.AllocMbPerSecond;
            double nb = TesselationStats.NeighbourAllocMbPerSecond;
            double rl = TesselationStats.RelightAllocMbPerSecond;
            if (total <= 0 || nb <= 0 || rl <= 0)
                throw new Exception($"alloc split missing: total {total:F1}, nachbarn {nb:F1}, licht {rl:F1}");
            if (Math.Abs(nb / total - 3.0 / 4.0) > 0.1)
                throw new Exception($"neighbour alloc share {nb / total:P0}, expected ~75%");
            if (Math.Abs(rl / total - 0.5 / 4.0) > 0.05)
                throw new Exception($"relight alloc share {rl / total:P0}, expected ~12.5%");
            double pt = TesselationStats.PartsAllocMbPerSecond;
            double js = TesselationStats.JsonAllocMbPerSecond;
            if (Math.Abs(pt / total - 0.25 / 4.0) > 0.03)
                throw new Exception($"parts alloc share {pt / total:P0}, expected ~6.3%");
            if (Math.Abs(js / total - 0.125 / 4.0) > 0.02)
                throw new Exception($"json alloc share {js / total:P0}, expected ~3.1%");
            TesselationStats.Reset();
        });

        Check("shadow throttling patch applies", () =>
        {
            ShadowThrottlePatches.Apply(harmony, farInterval: 2, nearInterval: 1, farMaxSkip: 4, moveThreshold: 0.5);
            ForceJit(triggerStage);
            if (ShadowThrottlePatches.FarInterval != 2) throw new Exception("interval not stored");
            if (ShadowThrottlePatches.FarMaxSkip != 4) throw new Exception("max skip not stored");

            // The live toggle's off state has to be EXACTLY vanilla: with 1/1/1 the decision
            // must be "render" on every frame, however long the session has run - that is the
            // contract that makes the always-applied patch safe as a default.
            ShadowThrottlePatches.ResetForTests();
            ShadowThrottlePatches.SetIntervals(1, 1, 1);
            if (ShadowThrottlePatches.Throttling) throw new Exception("1/1/1 still reports as throttling");
            for (long frame = 1; frame < 500; frame++)
                if (!ShadowThrottlePatches.WouldRenderFar(0.0, frame % 7 + 1, 1, 1, 0.15))
                    throw new Exception("1/1 skipped a frame - the toggle's off state is not vanilla");
            // and switching back on mid-session must actually throttle again
            ShadowThrottlePatches.SetIntervals(2, 1, 4);
            if (!ShadowThrottlePatches.Throttling) throw new Exception("2/4 not recognised as throttling");
            if (ShadowThrottlePatches.WouldRenderFar(0.0, 1, 2, 4, 0.15))
                throw new Exception("2/4 rendered on a frame the floor should skip");
        });

        Check("shadow throttle skips far and its Done stage together", () =>
        {
            ShadowThrottlePatches.ResetForTests();
            ShadowThrottlePatches.FarInterval = 2;
            ShadowThrottlePatches.FarMaxSkip = 4;
            ShadowThrottlePatches.NearInterval = 1;

            bool[] far = new bool[4];
            for (int frame = 0; frame < far.Length; frame++)
            {
                if (!ShadowThrottlePatches.SkipStage(null, EnumRenderStage.Before))
                    throw new Exception("the frame boundary stage must never be skipped");

                far[frame] = ShadowThrottlePatches.SkipStage(null, EnumRenderStage.ShadowFar);

                // OnRenderShadowFar pushes matrices that OnRenderShadowFarDone pops. If the two
                // stages ever disagree within a frame the matrix stack unbalances.
                if (ShadowThrottlePatches.SkipStage(null, EnumRenderStage.ShadowFarDone) != far[frame])
                    throw new Exception($"ShadowFarDone disagreed with ShadowFar in frame {frame}");

                if (!ShadowThrottlePatches.SkipStage(null, EnumRenderStage.ShadowNear))
                    throw new Exception("near cascade must render every frame at interval 1");
                if (!ShadowThrottlePatches.SkipStage(null, EnumRenderStage.Opaque))
                    throw new Exception("opaque must never be skipped");
            }

            // Without a ClientMain nothing can be said about camera or sun movement, so the
            // adaptive rule has to degrade to the plain interval: every other frame, exactly -
            // not "sometimes", not "never".
            if (far[0] == far[1] || far[1] == far[2] || far[2] == far[3])
                throw new Exception($"far cascade did not alternate: {string.Join(",", far)}");

            ShadowThrottlePatches.ResetForTests();
            ShadowThrottlePatches.FarInterval = 1;
            ShadowThrottlePatches.FarMaxSkip = 1;
            ShadowThrottlePatches.NearInterval = 1;
        });

        Check("camera movement overrides the shadow interval floor", () =>
        {
            // The artefact this guards against: a retained shadow map only *covers* the volume
            // it was drawn for. Compensating the sampling matrix keeps it correctly positioned
            // but cannot extend that coverage, so a camera that flies out of it samples past
            // the map edge, where the shader cuts the shadow off hard - a visible line that
            // jumps every time the cascade is finally redrawn. Applying the interval floor
            // while the camera is moving is exactly what produced it.
            const int interval = 2, maxSkip = 4;
            const double threshold = 0.15;

            // flying: about 0.35 blocks a frame at 30 blocks/s and 85 fps. Every frame after
            // the first must redraw, however small the interval says the gap may be.
            for (long since = 1; since <= 6; since++)
                if (!ShadowThrottlePatches.WouldRenderFar(0.35, since, interval, maxSkip, threshold))
                    throw new Exception($"skipped a frame {since} after moving 0.35 blocks - the map edge would show");

            // walking: ~0.05 blocks a frame, so the floor still applies and the saving remains
            if (ShadowThrottlePatches.WouldRenderFar(0.05, 1, interval, maxSkip, threshold))
                throw new Exception("redrew after a single walking frame - the saving is gone");

            // standing still: skip until the staleness cap
            for (long since = 1; since < maxSkip; since++)
                if (ShadowThrottlePatches.WouldRenderFar(0.0, since, interval, maxSkip, threshold))
                    throw new Exception($"redrew at frame {since} while standing still");
            if (!ShadowThrottlePatches.WouldRenderFar(0.0, maxSkip, interval, maxSkip, threshold))
                throw new Exception("never redrew even at the staleness cap");

            // and exactly at the threshold it must already count as moving
            if (!ShadowThrottlePatches.WouldRenderFar(threshold, 1, interval, maxSkip, threshold))
                throw new Exception("movement exactly at the threshold did not trigger");
        });

        Check("shadow throttle honours both the floor and the staleness cap", () =>
        {
            // The floor is what bounds the cost, the cap is what bounds how old the shadow map
            // may get. A rule that keeps one and loses the other is either a stutter or a bug
            // you only see when you stop walking.
            ShadowThrottlePatches.ResetForTests();
            ShadowThrottlePatches.FarInterval = 3;
            ShadowThrottlePatches.FarMaxSkip = 3;
            ShadowThrottlePatches.NearInterval = 1;

            var gaps = new List<int>();
            int lastRendered = -1;
            for (int frame = 0; frame < 30; frame++)
            {
                ShadowThrottlePatches.SkipStage(null, EnumRenderStage.Before);
                if (ShadowThrottlePatches.SkipStage(null, EnumRenderStage.ShadowFar))
                {
                    if (lastRendered >= 0) gaps.Add(frame - lastRendered);
                    lastRendered = frame;
                }
                ShadowThrottlePatches.SkipStage(null, EnumRenderStage.ShadowFarDone);
            }

            if (gaps.Count == 0) throw new Exception("far cascade never rendered");
            foreach (int gap in gaps)
                if (gap != 3) throw new Exception($"gap of {gap} frames, expected exactly 3");

            long total = ShadowThrottlePatches.FarRendered + ShadowThrottlePatches.FarSkipped;
            if (total != 30) throw new Exception($"counters do not add up: {total} of 30 frames");

            ShadowThrottlePatches.ResetForTests();
            ShadowThrottlePatches.FarInterval = 1;
            ShadowThrottlePatches.FarMaxSkip = 1;
        });

        Check("near cascade is out of phase with the far one", () =>
        {
            // Two cascades landing in the same frame is what turns throttling into judder:
            // the average is fine and every other frame is twice as long as its neighbour.
            ShadowThrottlePatches.ResetForTests();
            ShadowThrottlePatches.FarInterval = 2;
            ShadowThrottlePatches.FarMaxSkip = 2;
            ShadowThrottlePatches.NearInterval = 2;

            int bothInOneFrame = 0, neitherInOneFrame = 0;
            for (int frame = 0; frame < 12; frame++)
            {
                ShadowThrottlePatches.SkipStage(null, EnumRenderStage.Before);
                bool f = ShadowThrottlePatches.SkipStage(null, EnumRenderStage.ShadowFar);
                ShadowThrottlePatches.SkipStage(null, EnumRenderStage.ShadowFarDone);
                bool n = ShadowThrottlePatches.SkipStage(null, EnumRenderStage.ShadowNear);
                if (ShadowThrottlePatches.SkipStage(null, EnumRenderStage.ShadowNearDone) != n)
                    throw new Exception("ShadowNearDone disagreed with ShadowNear");
                if (f && n) bothInOneFrame++;
                if (!f && !n) neitherInOneFrame++;
            }

            if (bothInOneFrame != 0 || neitherInOneFrame != 0)
                throw new Exception($"{bothInOneFrame} frames drew both cascades, {neitherInOneFrame} drew none");

            ShadowThrottlePatches.ResetForTests();
            ShadowThrottlePatches.FarInterval = 1;
            ShadowThrottlePatches.FarMaxSkip = 1;
            ShadowThrottlePatches.NearInterval = 1;
        });

        Console.WriteLine(failures == 0 ? "\nall checks passed" : $"\n{failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Records that it was called, so dispatch order and count can be checked.</summary>
    private sealed class RecordingRenderer : IRenderer
    {
        private readonly List<string> log;
        private readonly string name;
        public RecordingRenderer(List<string> log, string name, double order = 0, int range = 0)
        { this.log = log; this.name = name; RenderOrder = order; RenderRange = range; }
        public double RenderOrder { get; }
        public int RenderRange { get; }
        public void OnRenderFrame(float dt, EnumRenderStage stage) => log.Add(name);
        public void Dispose() { }
    }

    /// <summary>Burns a guaranteed-visible amount of clock, so a timing wrapper around it can
    /// never legitimately read zero ticks - which keeps the every-frame-readable assertions
    /// deterministic instead of racing the Stopwatch resolution.</summary>
    private sealed class SpinningRenderer : IRenderer
    {
        public double RenderOrder => 0.4;
        public int RenderRange => 0;
        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            long until = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 100000;
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until) { }
        }
        public void Dispose() { }
    }

    /// <summary>The index byte ranges a pool would hand to glMultiDrawElements right now.</summary>
    private static List<(int start, int bytes)> Drawn(MeshDataPool pool)
    {
        var ranges = new List<(int, int)>();
        for (int i = 0; i < pool.indicesGroupsCount; i++)
            ranges.Add((pool.indicesStartsByte[i * 2], pool.indicesSizes[i] * 4));
        return ranges;
    }

    /// <summary>Whether a byte range lies entirely inside one of the emitted ranges.</summary>
    private static bool Covers(List<(int start, int bytes)> ranges, int start, int bytes)
    {
        foreach ((int s, int b) in ranges)
            if (start >= s && start + bytes <= s + b) return true;
        return false;
    }

    private static IEnumerable<MethodBase> GetAllMethods(Type t)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                               | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MethodBase m in t.GetMethods(all)) yield return m;
        foreach (MethodBase m in t.GetConstructors(all)) yield return m;
    }

    /// <summary>Every Harmony patch class the mod ships, discovered rather than listed.</summary>
    private static IEnumerable<Type> PatchClasses()
    {
        foreach (Type t in typeof(KometModSystem).Assembly.GetTypes())
        {
            if (t.Namespace != "Komet.Patches") continue;
            if (t.IsNested || !t.IsClass) continue;
            if (t.GetCustomAttribute<CompilerGeneratedAttribute>() != null) continue;
            yield return t;
        }
        yield return typeof(MeasurementPatches);
    }

    private static void DumpIl()
    {
        MethodInfo m = AccessTools.Method(
            AccessTools.TypeByName("Vintagestory.Client.NoObf.ChunkTesselatorManager"), "OnBeforeFrame");
        Console.WriteLine("\n--- ChunkTesselatorManager.OnBeforeFrame IL ---");
        int i = 0;
        foreach (CodeInstruction ins in PatchProcessor.GetOriginalInstructions(m))
            Console.WriteLine($"{i++,4} {ins}");
    }

    /// <summary>The nice value out of a /proc task directory - field 19 of stat, 1-indexed.</summary>
    private static int NiceOf(string taskDir)
    {
        string[] stat = System.IO.File.ReadAllText(taskDir + "/stat").Split(' ');
        return int.Parse(stat[18], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Records one visit per index, so a dropped or repeated slice is a hard failure
    /// rather than a rare rendering artefact.</summary>
    private sealed class CountingBody : IWorkBody
    {
        public int[] Hits;
        public void Run(int from, int to)
        {
            for (int i = from; i < to; i++) System.Threading.Interlocked.Increment(ref Hits[i]);
        }
    }

    private sealed class ThrowingBody : IWorkBody
    {
        public void Run(int from, int to) => throw new ArithmeticException("boom");
    }

    private static MeshDataPool NewPool()
    {
        ConstructorInfo ctor = typeof(MeshDataPool).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(int), typeof(int), typeof(int) }, null);
        var pool = (MeshDataPool)ctor.Invoke(new object[] { 500000, 750000, 512 });
        pool.indicesStartsByte = new int[1024];
        pool.indicesSizes = new int[512];

        var locations = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(pool);
        for (int i = 0; i < 64; i++)
        {
            locations.Add(new ModelDataPoolLocation
            {
                IndicesStart = i * 300,
                IndicesEnd = i * 300 + 300,
                LodLevel = i % 4,
                FrustumCullSphere = Sphere.BoundingSphereForCube((i % 8) * 32, 128, (i / 8) * 32, 32)
            });
        }
        return pool;
    }

    /// <summary>
    /// A pool of exactly <paramref name="count"/> parts, spread so the spatial grid gets several
    /// cells and the (cell, LOD) buckets come out at lengths that are deliberately not multiples
    /// of four - which is what puts work into the vector loop AND its scalar tail. Carries the
    /// awkward cases too: an out-of-range LOD level (vanilla: never visible), hidden parts, and
    /// parts whose chunk the occlusion pass marked invisible.
    /// </summary>
    private static MeshDataPool OddPool(int count)
    {
        ConstructorInfo ctor = typeof(MeshDataPool).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(int), typeof(int), typeof(int) }, null);
        var pool = (MeshDataPool)ctor.Invoke(new object[] { 500000, 750000, count + 8 });
        pool.indicesStartsByte = new int[(count + 8) * 2];
        pool.indicesSizes = new int[count + 8];

        var locations = AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations")(pool);
        for (int i = 0; i < count; i++)
        {
            var loc = new ModelDataPoolLocation
            {
                IndicesStart = i * 300,
                IndicesEnd = i * 300 + 300,
                // 5 levels over a 7-wide stride: bucket lengths land off multiples of four,
                // and level 4 is the out-of-range case vanilla's switch never draws
                LodLevel = (i * 3) % 5,
                FrustumCullSphere = Sphere.BoundingSphereForCube(
                    ((i * 7) % 23 - 11) * 32, 96 + (i % 3) * 32, ((i * 5) % 19 - 9) * 32, 32),
                Hide = (i % 29) == 0
            };
            if ((i % 11) == 0) loc.CullVisible = new Bools(false, false);
            locations.Add(loc);
        }
        return pool;
    }

    /// <summary>
    /// What a pool will actually draw, canonically: the index-buffer byte ranges with adjacent
    /// ones coalesced, plus the triangle counters. Two pools with the same string draw the same
    /// triangles in the same order, whether or not either merged its ranges.
    /// </summary>
    private static string Runs(MeshDataPool p)
    {
        var runs = new List<(int start, int end)>();
        for (int i = 0; i < p.indicesGroupsCount; i++)
        {
            int start = p.indicesStartsByte[i * 2];
            int end = start + p.indicesSizes[i] * 4;
            if (runs.Count > 0 && runs[^1].end == start) runs[^1] = (runs[^1].start, end);
            else runs.Add((start, end));
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(p.RenderedTriangles).Append('/').Append(p.AllocatedTris).Append(':');
        foreach ((int start, int end) in runs) sb.Append(' ').Append(start).Append('-').Append(end);
        return sb.ToString();
    }

    /// <summary>
    /// The longest light-space side of the box vanilla's ShadowBox.update would build: the AABB
    /// of the eight view frustum corners after the light transform. Reproduces the engine
    /// method exactly, including getCameraRotationMatrix returning the identity - which is why
    /// vanilla's box points along world -Z whatever way the player is looking.
    /// </summary>
    private static double VanillaBoxSpan(double[] lightView, double camX, double camY, double camZ,
                                         double r, double znear, double fov, double aspect)
    {
        double k = Math.Min(1.0, fov / 90.0);
        double farW = r * k, nearW = znear * k;
        double farH = farW / aspect, nearH = nearW / aspect;

        // identity rotation: forward = (0,0,-1), up = (0,1,0), right = forward x up = (1,0,0)
        double cnZ = camZ - znear, cfZ = camZ - r;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach ((double cy, double cz, double h, double w) in new[]
                 { (camY + farH, cfZ, 0.0, farW), (camY - farH, cfZ, 0.0, farW),
                   (camY + nearH, cnZ, 0.0, nearW), (camY - nearH, cnZ, 0.0, nearW) })
        foreach (int side in new[] { 1, -1 })
        {
            double px = camX + side * w, py = cy + h, pz = cz;
            double lx = lightView[0] * px + lightView[4] * py + lightView[8] * pz + lightView[12];
            double ly = lightView[1] * px + lightView[5] * py + lightView[9] * pz + lightView[13];
            if (lx < minX) minX = lx;
            if (lx > maxX) maxX = lx;
            if (ly < minY) minY = ly;
            if (ly > maxY) maxY = ly;
        }
        return Math.Max(maxX - minX, maxY - minY);
    }

    private static FrustumCulling NewCuller()
    {
        var culler = new FrustumCulling();
        culler.UpdateViewDistance(512);
        culler.lod0BiasSq = 200f * 200f;
        culler.lod2BiasSq = 400.0 * 400.0;
        culler.shadowRangeX = culler.shadowRangeZ = 220.0;

        double[] proj = Mat4d.Create();
        Mat4d.Perspective(proj, 70.0 * Math.PI / 180.0, 16.0 / 9.0, 0.3, 1024.0);
        double[] view = Mat4d.Create();
        Mat4d.LookAt(view, new double[] { 0, 140, 0 }, new double[] { 100, 130, 0 }, new double[] { 0, 1, 0 });
        culler.CalcFrustumEquations(new BlockPos(0, 140, 0, 0), proj, view);
        return culler;
    }

    /// <summary>Minimal concrete AnimatorBase so the ctor patch and OnFrame can be exercised.</summary>
    private sealed class TestAnimator : AnimatorBase
    {
        public TestAnimator(WalkSpeedSupplierDelegate walk, Animation[] anims) : base(walk, anims) { }
        public override int MaxJointId => 1;
        protected override void calculateMatrices(float dt) { }
    }
}
