namespace StackStateProof;

using System.Reflection;
using System.Runtime.CompilerServices;
using static StackStateProofCommon;

public static class GcFixture
{
    public static bool Enabled;
    public static WeakReference<CostTexture>? Texture;
    public static WeakReference<CostVec3[]>? Original, Replacement;
    public static long TextureBefore, OriginalBefore, ReplacementBefore;
    public static int Collections, TextureMoves, OriginalMoves, ReplacementMoves, StateChecks, DrawChecks;
    public static readonly List<object> Evidence = new();
    public static void Reset()
    {
        Enabled = false; Texture = null; Original = Replacement = null;
        Collections = TextureMoves = OriginalMoves = ReplacementMoves = StateChecks = DrawChecks = 0; Evidence.Clear();
    }
    // Addresses are diagnostic integers only, never used for reads/writes or retained as pointers.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Address<T>(T obj) where T : class => (long)Unsafe.As<T,nint>(ref obj);
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static CostTexture NewTexture()
    {
        _ = new byte[8192];
        var texture = new CostTexture { Id = 991 };
        Texture = new(texture); TextureBefore = Address(texture); return texture;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CaptureOriginal(CostVec3[] values)
    {
        Require(values.Length == 9 && values.All(v => v.X == 0 && v.Y == 0 && v.Z == 0), "GC original array not freshly zeroed");
        Original = new(values); OriginalBefore = Address(values);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CaptureReplacement(CostVec3[] values) { Replacement = new(values); ReplacementBefore = Address(values); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Collect()
    {
        for (int i=0; i<32; i++) GC.KeepAlive(new byte[4096]);
        GC.Collect(0, GCCollectionMode.Forced, true, true); Collections++;
    }
    public static void Slice(ref CostVec3[] values)
    {
        CaptureOriginal(values);
        // Actual SlicedBlock uses a local array alias; replacing this ref must NOT write
        // back into the scratch field. The original is now rooted by scratch, not this alias.
        values = new CostVec3[9]; CaptureReplacement(values);
        Collect();
        Require(Texture!.TryGetTarget(out var texture), "scratch-only texture died under GC");
        Require(Original!.TryGetTarget(out var original), "scratch-only original array died under GC");
        Require(Replacement!.TryGetTarget(out var replacement), "helper-local replacement array died under GC");
        long textureAfter=Address(texture!), originalAfter=Address(original!), replacementAfter=Address(replacement!);
        Require(ReferenceEquals(values,replacement), "byref local array lost replacement");
        if(textureAfter!=TextureBefore)TextureMoves++; if(originalAfter!=OriginalBefore)OriginalMoves++; if(replacementAfter!=ReplacementBefore)ReplacementMoves++;
        Evidence.Add(new {TextureBefore,textureAfter,OriginalBefore,originalAfter,ReplacementBefore,replacementAfter, generation=0,compactRequested=true,onlyWeakObserverRoots=true});
    }
    public static void State<T>(ref T state,string method,bool after)
    {
        if(!after || method!="DrawSingleTile_SlicedBlock" || Original==null)return;
        object boxed=state!;
        var original=(CostVec3[])boxed.GetType().GetField("colorSlices")!.GetValue(boxed)!;
        Require(Original.TryGetTarget(out var expected)&&ReferenceEquals(original,expected),"lighting local array replacement leaked into scratch field");
        Require(original.All(v=>v.X==0&&v.Y==0&&v.Z==0),"original scratch array mutated through replaced local");
        StateChecks++;
    }
    public static void Draw(CostTexture value)
    {
        if(!Enabled)return;
        Require(Texture!=null&&Texture.TryGetTarget(out var expected)&&ReferenceEquals(expected,value)&&value.Id==991,"scratch texture not visible after compacting GC"); DrawChecks++;
    }
}
