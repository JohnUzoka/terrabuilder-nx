// These shapes exist only in the patch tool and are mapped to target game types.
internal sealed class ReuseOwnerShape { }
internal sealed class ReuseScratchShape
{
    internal Array colorSlices = null!;
    internal void NXReset46(Array ownedSlices) => throw new NotSupportedException();
}
internal sealed class ReuseScopeShape
{
    internal ReuseOwnerShape Owner = null!;
    internal ReuseScratchShape? Scratch;
    internal Array? Slices;
    internal bool Busy;
}
internal static class ReuseTemplate
{
    [ThreadStatic] private static ReuseScopeShape? current;
    static ReuseTemplate() { }

    internal static ReuseScopeShape? Enter(ReuseOwnerShape owner)
    {
        var previous = current;
        current = new ReuseScopeShape { Owner = owner };
        return previous;
    }
    internal static void Exit(ReuseScopeShape? previous) => current = previous;

    internal static ReuseScratchShape Acquire(ReuseOwnerShape owner, out ReuseScopeShape? lease)
    {
        lease = null;
        var scope = current;
        // Preserve fresh storage for unscoped, other-owner or recursive calls.
        if (scope == null || scope.Owner != owner || scope.Busy)
            return new ReuseScratchShape();
        lease = scope;
        scope.Busy = true;
        if (scope.Scratch == null)
        {
            var scratch = new ReuseScratchShape();
            scope.Scratch = scratch;
            scope.Slices = scratch.colorSlices;
        }
        else
        {
            scope.Scratch.NXReset46(scope.Slices!);
        }
        return scope.Scratch;
    }
    internal static void Release(ReuseScopeShape? lease)
    {
        if (lease != null) lease.Busy = false;
    }
}
