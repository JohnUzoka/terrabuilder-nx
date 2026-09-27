using System.Reflection;
using System.Runtime.CompilerServices;
using static RenderProof.Session;

internal static class RenderBatchProof
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    sealed record Snapshot(RenderEvent[] Events, string State, Exception? Error, int Result, long Effects);
    sealed record Chunk(int Offset, int Count, int Position, RenderSetDataOptions Options);

    internal static object Run(RenderProof.Session p, string output)
    {
        var beforeEnd = p.Hooks.GetMethod("BaselineBatchEnd")!.CreateDelegate<Func<RenderBatch, int>>();
        var afterEnd = p.Hooks.GetMethod("PatchedBatchEnd")!.CreateDelegate<Func<RenderBatch, int>>();
        var beforeRender = p.Hooks.GetMethod("BaselineRenderBatch")!.CreateDelegate<Action<RenderBatch, RenderTexture, RenderSpriteData[], int, int>>();
        var afterRender = p.Hooks.GetMethod("PatchedRenderBatch")!.CreateDelegate<Action<RenderBatch, RenderTexture, RenderSpriteData[], int, int>>();
        var beforeLayered = p.Hooks.GetMethod("BaselineFlushLayered")!.CreateDelegate<Action<RenderBatch>>();
        var afterLayered = p.Hooks.GetMethod("PatchedFlushLayered")!.CreateDelegate<Action<RenderBatch>>();
        var uploadOffset = p.Runtime.GetMethod("BatchUpload001", Flags)!.CreateDelegate<RenderUploadOffset>();
        var uploadArray = p.Runtime.GetMethod("BatchUpload000", Flags)!.CreateDelegate<RenderUploadArray>();
        var submit = p.Runtime.GetMethod("BatchSubmit002", Flags)!.CreateDelegate<RenderSubmit>();
        var begin = p.Runtime.GetMethod("FrameBegin", Flags)!.CreateDelegate<Func<int>>();
        var end = p.Runtime.GetMethod("FrameEnd", Flags)!.CreateDelegate<Action<int, bool>>();
        var rows = new List<object>();
        var texture = new RenderTexture { Id = 73 };
        var sprites = Enumerable.Range(0, 6000).Select(i => new RenderSpriteData { Rotation = i * 3 + 11 }).ToArray();
        var batch = new RenderBatch();
        var device = batch._graphicsDevice;
        var buffer = batch._vertexBuffer;
        var vertices = batch._vertices;
        var main = new FixtureMain();
        var time = new FixtureTime();
        var realFailure = new InvalidOperationException("same game-side boundary exception object");

        void ResetBatch()
        {
            batch.Trace.Reset(); batch._graphicsDevice = device; batch._vertexBuffer = buffer; batch._vertices = vertices;
            device.Draws = buffer.Uploads = 0; Array.Clear(device.TextureSlots.Slots); Array.Clear(vertices);
            batch._layeredSortingEnabled = true; batch._queuedSpriteCount = 0; batch._passTextureCount = 0;
            batch._drawCalls = 9; batch._vertexBufferPosition = 0; batch._batchCount = 0; batch._batchDataCount = 0;
            batch._batchLookup.Clear(); batch._textureIdLookup.Clear(); Array.Clear(batch._batchLookupCache);
            batch._currentBatchKey = default; batch.OnFlush = batch.OnFlushLayered = null;
        }
        string State()
        {
            long hash = 19;
            foreach (var vertex in vertices) hash = unchecked(hash * 31 + BitConverter.SingleToInt32Bits(vertex.Position.X));
            string cache = string.Join(",", batch._batchLookupCache.Select(c => $"{c.Texture?.Id}:{c.BatchIndex}"));
            return $"{batch._layeredSortingEnabled}:{batch._queuedSpriteCount}:{batch._passTextureCount}:{batch._drawCalls}:{batch._vertexBufferPosition}:{batch._batchCount}:{batch._batchDataCount}:" +
                $"{device.Draws}:{buffer.Uploads}:{device.TextureSlots.Slots[0]?.Id}:{batch._batchLookup.Count}:{batch._textureIdLookup.Count}:{cache}:{batch._currentBatchKey.Texture?.Id}:{batch._currentBatchKey.LayerStack}:{hash}:" +
                string.Join(",", batch._batches.Select(b => $"{b.LayerStack}/{b.Texture}/{b.Head}/{b.Length}"));
        }
        Snapshot Execute(bool candidate, bool root, Action setup, Func<bool, int> action)
        {
            p.Reset(); ResetBatch(); setup(); RenderFixture.Step = 0;
            Exception? error = null; int result = int.MinValue;
            if (root)
            {
                FixtureMain.OnPostDraw = _ => { try { result = action(candidate); } catch (Exception caught) { error = caught; } };
                (candidate ? p.AfterMain : p.BeforeMain)(main, time);
                FixtureMain.OnPostDraw = null;
            }
            else { try { result = action(candidate); } catch (Exception caught) { error = caught; } }
            return new(batch.Trace.Events.ToArray(), State(), error, result, FixtureMain.Effects);
        }
        void Same(Snapshot left, Snapshot right, string name)
        {
            p.Check(left.Events.SequenceEqual(right.Events), name + ": exact receiver, array, texture, arguments and event order");
            p.Check(left.State == right.State && left.Result == right.Result && left.Effects == right.Effects, name + ": state, vertices, return and root effects");
            p.Check(left.Error == null ? right.Error == null : left.Error is NullReferenceException ? right.Error is NullReferenceException : ReferenceEquals(left.Error, right.Error), name + ": exact real exception identity or original null-receiver behavior");
        }
        void Accounting(Snapshot candidate, bool root, int endCalls, string name)
        {
            object totals = p.Get("WindowBatch");
            bool valid = root && candidate.Error == null;
            p.Check(Number(totals, "Frames") == (root ? 1 : 0), name + ": eligible root frame attribution");
            p.Check(Number(totals, "ValidFrames") == (valid ? 1 : 0), name + ": valid frame attribution");
            if (!root) p.Check(RenderFixture.ClockCalls == 0, name + ": inactive emitted scopes do not read clock");
            foreach (var (field, kind, alternate, expected) in new[] {
                ("End", (RenderBoundary?)null, (RenderBoundary?)null, endCalls),
                ("Upload", (RenderBoundary?)RenderBoundary.UploadOffset, (RenderBoundary?)RenderBoundary.UploadArray, -1),
                ("Submit", (RenderBoundary?)RenderBoundary.Submit, (RenderBoundary?)null, -1) })
            {
                long calls = expected >= 0 ? expected : candidate.Events.Count(e => e.Kind == kind || e.Kind == alternate);
                object metric = Field(totals, field);
                p.Check(Number(metric, "Calls") == (valid ? calls : 0), name + ": emitted " + field + " scope count");
                if (valid && field != "End")
                    p.Check(Number(metric, "InclusiveTicks") == calls * 17 && Number(metric, "ExclusiveTicks") == calls * 17,
                        name + ": emitted " + field + " exact deterministic interval");
            }
        }
        Snapshot Compare(string name, bool root, Action setup, Func<bool, int> action, int endCalls = 0, bool account = true)
        {
            Snapshot baseline = Execute(false, root, setup, action);
            Snapshot control = Execute(false, root, setup, action);
            Same(baseline, control, name + ": baseline replay control");
            Snapshot candidate = Execute(true, root, setup, action);
            Same(baseline, candidate, name);
            if (account) Accounting(candidate, root, endCalls, name);
            rows.Add(new { name, root, events = candidate.Events.Length, result = candidate.Result, exception = candidate.Error?.GetType().Name, baselineReplay = true });
            return candidate;
        }
        void RenderOracle(Snapshot snapshot, Chunk[] chunks, int finalPosition, string name)
        {
            p.Check(snapshot.Error == null && snapshot.Events.Length == 2 + 3 * chunks.Length, name + ": fixed external boundary count");
            p.Check(snapshot.Events[0] == new RenderEvent(RenderBoundary.Textures, device) &&
                snapshot.Events[1] == new RenderEvent(RenderBoundary.TextureSet, device.TextureSlots, Texture: texture), name + ": fixed texture receiver and value");
            for (int i = 0; i < chunks.Length; i++)
            {
                Chunk c = chunks[i]; int at = 2 + i * 3;
                p.Check(snapshot.Events[at] == new RenderEvent(RenderBoundary.FillSprites, batch, sprites, texture, c.Offset, c.Count, 0), name + ": fixed fill arguments " + i);
                p.Check(snapshot.Events[at + 1] == new RenderEvent(RenderBoundary.UploadOffset, buffer, vertices, A: c.Position * 96, B: 0, C: c.Count * 4, D: 24, E: (int)c.Options, Element: typeof(RenderVertex)), name + ": fixed upload arguments " + i);
                p.Check(snapshot.Events[at + 2] == new RenderEvent(RenderBoundary.Submit, device, A: 0, B: 0, C: c.Position * 4, D: c.Count * 4, E: c.Position * 6, F: c.Count * 2), name + ": fixed indexed arguments " + i);
            }
            p.Check(batch._vertexBufferPosition == finalPosition && batch._drawCalls == 9 + chunks.Length, name + ": fixed caller state");
            p.Check(ReferenceEquals(batch._vertices, vertices) && ReferenceEquals(batch._graphicsDevice, device) && ReferenceEquals(batch._vertexBuffer, buffer), name + ": no receiver or array replacement");
            if (chunks.Length != 0)
            {
                Chunk last = chunks[^1];
                p.Check(vertices[0].Position.X == (last.Offset * 3 + 11) && vertices[last.Count * 4 - 1].Position.X == ((last.Offset + last.Count - 1) * 3 + 14),
                    name + ": independently fixed private-fill sentinel output");
            }
        }
        var renderCases = new[] {
            ("zero", 37, 0, 37, Array.Empty<Chunk>()),
            ("positive", 13, 7, 20, new[] { new Chunk(5, 7, 13, RenderSetDataOptions.NoOverwrite) }),
            ("full", 0, 2048, 2048, new[] { new Chunk(5, 2048, 0, RenderSetDataOptions.NoOverwrite) }),
            ("full-buffer-discard", 2048, 1, 1, new[] { new Chunk(5, 1, 0, RenderSetDataOptions.Discard) }),
            ("short-remainder-discard", 1800, 300, 300, new[] { new Chunk(5, 300, 0, RenderSetDataOptions.Discard) }),
            ("boundary-remainder", 1792, 300, 44, new[] { new Chunk(5, 256, 1792, RenderSetDataOptions.NoOverwrite), new Chunk(261, 44, 0, RenderSetDataOptions.Discard) }),
            ("multiple-chunks", 0, 4353, 257, new[] { new Chunk(5, 2048, 0, RenderSetDataOptions.NoOverwrite), new Chunk(2053, 2048, 0, RenderSetDataOptions.Discard), new Chunk(4101, 257, 0, RenderSetDataOptions.Discard) })
        };
        foreach (bool root in new[] { false, true })
        foreach (var (label, position, count, finalPosition, chunks) in renderCases)
        {
            string name = "render-" + label + (root ? "-active-root" : "-inactive");
            Snapshot result = Compare(name, root, () => batch._vertexBufferPosition = position,
                candidate => { (candidate ? afterRender : beforeRender)(batch, texture, sprites, 5, count); return 0; });
            RenderOracle(result, chunks, finalPosition, name);
        }
        var changedDevice = new RenderGraphicsDevice(batch.Trace);
        var changedBuffer = new RenderDynamicVertexBuffer(batch.Trace);
        var changedVertices = new RenderVertex[8192];
        foreach (bool root in new[] { false, true })
        {
            Snapshot changed = Compare("evaluated-receiver-mutations-" + root, root, () => {
                changedDevice.Draws = changedBuffer.Uploads = 0; Array.Clear(changedVertices);
                batch.Trace.OnBoundary = e => {
                    if (e.Kind == RenderBoundary.FillSprites) { batch._vertexBuffer = changedBuffer; batch._vertices = changedVertices; batch._vertexBufferPosition = 17; }
                    if (e.Kind == RenderBoundary.UploadOffset) batch._graphicsDevice = changedDevice;
                };
            }, candidate => { (candidate ? afterRender : beforeRender)(batch, texture, sprites, 5, 7); return 0; });
            p.Check(changed.Events[3] == new RenderEvent(RenderBoundary.UploadOffset, changedBuffer, changedVertices, A: 1632, B: 0, C: 28, D: 24, E: 2, Element: typeof(RenderVertex)) &&
                changed.Events[4] == new RenderEvent(RenderBoundary.Submit, changedDevice, A: 0, B: 0, C: 68, D: 28, E: 102, F: 14),
                "dynamic caller reads preserve exactly evaluated buffer, array, graphics receiver and arguments " + root);
            p.Check(changedBuffer.Uploads == 1 && changedDevice.Draws == 1 && batch._vertexBufferPosition == 24 && changedVertices[0].Position.X == 26,
                "dynamic evaluated-receiver fixed output control " + root);
        }

        void LayerSetup(int[] lengths)
        {
            batch._batches = lengths.Select((length, i) => new RenderLayerBatch { Length = length, Head = i * 5000, Texture = 0, LayerStack = (uint)i }).ToArray();
            batch._batchCount = lengths.Length; batch._queuedSpriteCount = lengths.Sum(); batch._passTextureCount = 1; batch._batchDataCount = 7;
            batch._passTextures = new[] { texture }; batch._vertexBufferPosition = 53;
            batch._currentBatchKey = new() { Texture = texture, LayerStack = 99 };
            batch._batchLookup[batch._currentBatchKey] = 42; batch._textureIdLookup[texture] = 0;
            batch._batchLookupCache[0] = new() { Texture = texture, BatchIndex = 42 };
        }
        foreach (bool root in new[] { false, true })
        foreach (var (name, lengths, uploads, counts, bases, finalPosition) in new[] {
            ("empty", Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), 0),
            ("zero-batch", new[] { 0 }, new[] { 0 }, new[] { 0 }, new[] { 0 }, 0),
            ("positive", new[] { 7 }, new[] { 7 }, new[] { 7 }, new[] { 0 }, 7),
            ("full", new[] { 2048 }, new[] { 2048 }, new[] { 2048 }, new[] { 0 }, 2048),
            ("remainder", new[] { 2305 }, new[] { 2048, 257 }, new[] { 2048, 257 }, new[] { 0, 0 }, 257),
            ("multiple-chunks", new[] { 4353 }, new[] { 2048, 2048, 257 }, new[] { 2048, 2048, 257 }, new[] { 0, 0, 0 }, 257),
            ("packed-batches", new[] { 300, 500 }, new[] { 800 }, new[] { 300, 500 }, new[] { 0, 300 }, 800) })
        {
            string label = "layered-" + name + (root ? "-active-root" : "-inactive");
            Snapshot result = Compare(label, root, () => LayerSetup(lengths), candidate => { (candidate ? afterLayered : beforeLayered)(batch); return 0; });
            var actualUploads = result.Events.Where(e => e.Kind == RenderBoundary.UploadArray).ToArray();
            var actualSubmits = result.Events.Where(e => e.Kind == RenderBoundary.Submit).ToArray();
            p.Check(actualUploads.Length == uploads.Length && actualSubmits.Length == counts.Length, label + ": fixed chunk counts");
            for (int i = 0; i < uploads.Length; i++)
                p.Check(actualUploads[i] == new RenderEvent(RenderBoundary.UploadArray, buffer, vertices, A: 0, B: uploads[i] * 4, C: 1, Element: typeof(RenderVertex)), label + ": fixed array upload " + i);
            for (int i = 0; i < counts.Length; i++)
                p.Check(actualSubmits[i] == new RenderEvent(RenderBoundary.Submit, device, A: 0, B: bases[i] * 4, C: 0, D: counts[i] * 4, E: 0, F: counts[i] * 2), label + ": fixed indexed submission " + i);
            p.Check(batch._vertexBufferPosition == finalPosition && batch._drawCalls == 9 + counts.Length && batch._queuedSpriteCount == 0 && batch._batchDataCount == 0 && batch._batchCount == 0 && batch._passTextureCount == 0,
                label + ": fixed completion counters");
            p.Check(batch._batchLookup.Count == 0 && batch._textureIdLookup.Count == 0 && batch._batchLookupCache.All(c => c.Texture == null && c.BatchIndex == 0) && batch._currentBatchKey.Texture == null && batch._currentBatchKey.LayerStack == 0,
                label + ": exact clear and initobj completion effects");
        }
        foreach (bool root in new[] { false, true })
        {
            Snapshot empty = Compare("end-empty-" + root, root, () => { }, candidate => (candidate ? afterEnd : beforeEnd)(batch), 1);
            p.Check(empty.Result == 0 && empty.Events.Length == 0 && !batch._layeredSortingEnabled, "End zero return and sorting write fixed oracle " + root);
            Snapshot plain = Compare("end-flush-" + root, root, () => { batch._queuedSpriteCount = 3; batch.OnFlush = b => { b._drawCalls += 7; b._queuedSpriteCount = 0; }; }, candidate => (candidate ? afterEnd : beforeEnd)(batch), 1);
            p.Check(plain.Result == 16 && plain.Events.Select(e => e.Kind).SequenceEqual(new[] { RenderBoundary.FlushRenderState, RenderBoundary.Flush }) && !batch._layeredSortingEnabled,
                "End nonlayered dispatch, integer return and sorting fixed oracle " + root);
            Snapshot layered = Compare("end-layered-" + root, root, () => LayerSetup(new[] { 7 }), candidate => {
                batch.OnFlushLayered = candidate ? afterLayered : beforeLayered;
                return (candidate ? afterEnd : beforeEnd)(batch);
            }, 1);
            p.Check(layered.Result == 10 && layered.Events.Count(e => e.Kind == RenderBoundary.UploadArray) == 1 && layered.Events.Count(e => e.Kind == RenderBoundary.Submit) == 1 && !batch._layeredSortingEnabled,
                "End layered nested actual caller and return fixed oracle " + root);
        }

        foreach (RenderBoundary failure in new[] { RenderBoundary.FillSprites, RenderBoundary.UploadOffset, RenderBoundary.Submit })
        {
            string name = "render-real-throw-" + failure;
            Snapshot result = Compare(name, true, () => { batch.Trace.ThrowAt = failure; batch.Trace.Failure = realFailure; }, candidate => { (candidate ? afterRender : beforeRender)(batch, texture, sprites, 5, 7); return 0; }, account: false);
            p.Check(ReferenceEquals(result.Error, realFailure) && batch._drawCalls == 9 && batch._vertexBufferPosition == 0, name + ": same original exception and no post-call writes");
            if (failure != RenderBoundary.FillSprites)
                p.Check(Number(p.Get("WindowBatch"), "DiscardedFrames") == 1 && Number(p.Get("WindowBatch"), "AbortedScopes") == 1 && !(bool)p.Get("MeasurementInvalid"), name + ": caught game scope abort discards without observer invalidity");
        }
        foreach (RenderBoundary failure in new[] { RenderBoundary.FlushRenderState, RenderBoundary.UploadArray, RenderBoundary.Submit })
        {
            string name = "end-real-throw-" + failure;
            Snapshot result = Compare(name, true, () => { LayerSetup(new[] { 7 }); batch.Trace.ThrowAt = failure; batch.Trace.Failure = realFailure; }, candidate => {
                batch.OnFlushLayered = candidate ? afterLayered : beforeLayered; return (candidate ? afterEnd : beforeEnd)(batch);
            }, 1, account: false);
            p.Check(ReferenceEquals(result.Error, realFailure) && Number(p.Get("WindowBatch"), "DiscardedFrames") == 1 && !(bool)p.Get("MeasurementInvalid"), name + ": nested original abort discards whole frame");
            p.Check(Number(Field(p.Get("WindowBatch"), "End"), "Calls") == 0 && Number(Field(p.Get("WindowBatch"), "Upload"), "Calls") == 0 && Number(Field(p.Get("WindowBatch"), "Submit"), "Calls") == 0, name + ": no partial donation");
        }
        Compare("render-null-upload-receiver", true, () => batch._vertexBuffer = null!, candidate => { (candidate ? afterRender : beforeRender)(batch, texture, sprites, 5, 7); return 0; }, account: false);
        Compare("render-null-graphics-receiver", true, () => batch._graphicsDevice = null!, candidate => { (candidate ? afterRender : beforeRender)(batch, texture, sprites, 5, 7); return 0; }, account: false);

        void WrapperPair(string name, Action baseline, Action candidate, RenderEvent expected, bool active)
        {
            p.Reset(); ResetBatch(); baseline(); var expectedEvents = batch.Trace.Events.ToArray();
            p.Check(expectedEvents.SequenceEqual(new[] { expected }), name + ": baseline fixed sentinel arguments");
            p.Reset(); ResetBatch(); RenderFixture.Step = 0; int depth = active ? begin() : 0;
            candidate(); if (active) end(depth, true);
            p.Check(batch.Trace.Events.SequenceEqual(expectedEvents), name + ": actual emitted wrapper keeps each distinct argument");
            p.Check(active || RenderFixture.ClockCalls == 0, name + ": inactive clock guard");
        }
        foreach (bool active in new[] { false, true })
        {
            WrapperPair("offset-overload-sentinels-" + active,
                () => buffer.SetData(113, vertices, 7, 19, 29, RenderSetDataOptions.NoOverwrite),
                () => uploadOffset(buffer, 113, vertices, 7, 19, 29, RenderSetDataOptions.NoOverwrite),
                new(RenderBoundary.UploadOffset, buffer, vertices, A: 113, B: 7, C: 19, D: 29, E: 2, Element: typeof(RenderVertex)), active);
            WrapperPair("array-overload-sentinels-" + active,
                () => buffer.SetData(vertices, 11, 23, RenderSetDataOptions.Discard),
                () => uploadArray(buffer, vertices, 11, 23, RenderSetDataOptions.Discard),
                new(RenderBoundary.UploadArray, buffer, vertices, A: 11, B: 23, C: 1, Element: typeof(RenderVertex)), active);
            WrapperPair("indexed-sentinels-" + active,
                () => device.DrawIndexedPrimitives(RenderPrimitiveType.LineStrip, 13, 17, 23, 31, 41),
                () => submit(device, RenderPrimitiveType.LineStrip, 13, 17, 23, 31, 41),
                new(RenderBoundary.Submit, device, A: 3, B: 13, C: 17, D: 23, E: 31, F: 41), active);
            WrapperPair("null-array-identity-" + active,
                () => buffer.SetData<RenderVertex>(null!, 3, 5, RenderSetDataOptions.Discard),
                () => uploadArray(buffer, null!, 3, 5, RenderSetDataOptions.Discard),
                new(RenderBoundary.UploadArray, buffer, null, A: 3, C: 1, B: 5, Element: typeof(RenderVertex)), active);
        }
        foreach (var (label, baseline, candidate) in new (string, Action, Action)[] {
            ("offset", () => ((RenderDynamicVertexBuffer)null!).SetData(1, vertices, 2, 3, 4, RenderSetDataOptions.Discard), () => uploadOffset(null!, 1, vertices, 2, 3, 4, RenderSetDataOptions.Discard)),
            ("array", () => ((RenderDynamicVertexBuffer)null!).SetData(vertices, 2, 3, RenderSetDataOptions.Discard), () => uploadArray(null!, vertices, 2, 3, RenderSetDataOptions.Discard)),
            ("submit", () => ((RenderGraphicsDevice)null!).DrawIndexedPrimitives(RenderPrimitiveType.LineList, 1, 2, 3, 4, 5), () => submit(null!, RenderPrimitiveType.LineList, 1, 2, 3, 4, 5)) })
        {
            p.Reset(); Exception? expected = null, observed = null;
            try { baseline(); } catch (Exception e) { expected = e; }
            int depth = begin(); try { candidate(); } catch (Exception e) { observed = e; } end(depth, true);
            p.Check(expected is NullReferenceException && observed is NullReferenceException && Number(p.Get("WindowBatch"), "DiscardedFrames") == 1, "typed " + label + " null callvirt behavior through active emitted wrapper");
        }

        p.Reset(); ResetBatch(); RenderFixture.Step = 0; int nesting = begin(); bool entered = false;
        batch.Trace.OnBoundary = e => { if (!entered && e.Kind == RenderBoundary.UploadOffset) { entered = true; submit(device, RenderPrimitiveType.LineList, 2, 3, 4, 5, 6); } };
        uploadOffset(buffer, 13, vertices, 3, 7, 24, RenderSetDataOptions.Discard); end(nesting, true);
        p.Check(Unsafe.SizeOf<RenderVertex>() == 24, "fixture vertex size agrees with pinned FNA upload stride");
        object nestedTotals = p.Get("WindowBatch");
        p.Check(Number(Field(nestedTotals, "Upload"), "Calls") == 1 && Number(Field(nestedTotals, "Upload"), "InclusiveTicks") == 34 && Number(Field(nestedTotals, "Upload"), "ExclusiveTicks") == 17 && Number(Field(nestedTotals, "Submit"), "InclusiveTicks") == 17,
            "reentrant actual emitted upload and indexed wrappers preserve immediate-child exclusivity");
        p.Check(batch.Trace.Events.Select(e => e.Kind).SequenceEqual(new[] { RenderBoundary.UploadOffset, RenderBoundary.Submit }), "reentrant wrapper boundary order preserved");

        p.Reset(); ResetBatch(); RenderFixture.Step = 0; int clockDepth = begin(); RenderFixture.ThrowClock = true;
        afterRender(batch, texture, sprites, 5, 7); RenderFixture.ThrowClock = false; end(clockDepth, true);
        p.Check(device.Draws == 1 && buffer.Uploads == 1 && batch._drawCalls == 10 && batch._vertexBufferPosition == 7, "observer clock failure cannot skip original emitted caller operations");
        p.Check((bool)p.Get("MeasurementInvalid") && Number(p.Get("WindowBatch"), "DiscardedFrames") == 1, "observer clock failure discards emitted wrapper frame");

        foreach (bool root in new[] { false, true })
        {
            bool recursed = false;
            Snapshot recursive = Compare("recursive-End-" + root, root,
                () => { batch._queuedSpriteCount = 1; recursed = false; }, candidate => {
                    batch.OnFlush = b => { if (!recursed) { recursed = true; _ = (candidate ? afterEnd : beforeEnd)(b); } };
                    return (candidate ? afterEnd : beforeEnd)(batch);
                }, 2);
            p.Check(recursive.Result == 9 && recursive.Events.Select(e => e.Kind).SequenceEqual(new[] {
                RenderBoundary.FlushRenderState, RenderBoundary.Flush, RenderBoundary.FlushRenderState, RenderBoundary.Flush }),
                "recursive actual End preserves receiver, dispatch, order and return " + root);
            if (root)
            {
                object metric = Field(p.Get("WindowBatch"), "End");
                p.Check(Number(metric, "InclusiveTicks") == 102 && Number(metric, "ExclusiveTicks") == 68,
                    "recursive actual End retains outer scope and subtracts only its immediate child");
            }
        }
        Compare("End-null-receiver", true, () => { }, candidate => (candidate ? afterEnd : beforeEnd)(null!), 1, account: false);

        var allocations = new List<object>();
        foreach (bool active in new[] { false, true })
        {
            p.Reset(); ResetBatch(); batch.Trace.Capture = false; RenderFixture.Step = 0; int depth = active ? begin() : 0;
            batch._batches = new[] { new RenderLayerBatch { Length = 1 } }; batch._passTextures = new[] { texture };
            void Round()
            {
                batch._vertexBufferPosition = 0; afterRender(batch, texture, sprites, 5, 1);
                uploadArray(buffer, vertices, 1, 4, RenderSetDataOptions.Discard);
                submit(device, RenderPrimitiveType.LineList, 1, 2, 3, 4, 5);
                batch._queuedSpriteCount = 0; _ = afterEnd(batch);
                batch._batchCount = 1; batch._queuedSpriteCount = 1; batch._passTextureCount = 1;
                afterLayered(batch);
            }
            for (int i = 0; i < 256; i++) Round();
            long started = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++) Round();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - started;
            if (active) end(depth, true);
            p.Check(bytes == 0, "warmed emitted End/RenderBatch/FlushLayered/both-upload-overloads/indexed wrappers allocate zero bytes active=" + active);
            allocations.Add(new { active, iterations = 2000, allocatedBytes = bytes, externalBoundaryEventsCaptured = false });
        }
        p.Reset();
        return new { passed = true, cases = rows, allocations, fixtureVertexSize = Unsafe.SizeOf<RenderVertex>(),
            exactCallers = new[] { "TileBatch.End", "TileBatch.RenderBatch", "TileBatch.FlushLayered" },
            boundaries = "GraphicsDevice/TextureCollection/DynamicVertexBuffer, private FillVertexBuffer, LayerBatch comparer, FlushRenderState and nonlayered Flush are deterministic external fixtures; original/candidate caller and wrapper IL is executed. These fixtures do not execute a GPU or implement game geometry.",
            controls = "Each caller scenario replays the same baseline twice on identical receiver/array identities. Independent fixed chunk, return, state and argument oracles reject shared fixture/comparison mistakes." };
    }
}
