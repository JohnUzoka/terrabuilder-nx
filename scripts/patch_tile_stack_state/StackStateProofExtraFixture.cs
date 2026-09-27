namespace StackStateProof;

public sealed class CostChest { public int frame; public static int FindChest(int x,int y) { CostFixture.Effect("Chest.FindChest"); CostFixture.Value(x);CostFixture.Value(y);return 0; } }
public sealed class CostDummy { public int npc; }
public sealed class CostNpc { public CostRect frame; }
public static class CostEntity
{
    public static int Find(int x,int y) { CostFixture.Effect("Dummy.Find");CostFixture.Value(x);CostFixture.Value(y);return 0; }
    public static bool TryGet(int id,out CostDummy dummy) { CostFixture.Effect("Dummy.TryGet");CostFixture.Value(id);dummy=new(){npc=0};return true; }
}
public static class CostMinecart
{
    public static void TrackColors(int x,int y,CostCell tile,ref int left,ref int right){CostFixture.Effect("TrackColors");CostFixture.Value(x);CostFixture.Value(y);CostFixture.Value(tile.type);left=2;right=3;}
    public static CostRect GetSourceRect(int frame,int style){CostFixture.Effect("Track.SourceRect");CostFixture.Value(frame);CostFixture.Value(style);return new(frame*18,style*18,16,16);}
    public static bool DrawLeftDecoration(int frame){CostFixture.Effect("Track.Left");CostFixture.Value(frame);return (frame&1)==0;}
    public static bool DrawRightDecoration(int frame){CostFixture.Effect("Track.Right");CostFixture.Value(frame);return (frame&1)!=0;}
    public static bool DrawBumper(int frame){CostFixture.Effect("Track.Bumper");CostFixture.Value(frame);return frame==2;}
    public static bool DrawBouncyBumper(int frame){CostFixture.Effect("Track.Bouncy");CostFixture.Value(frame);return frame==3;}
}
public struct CostCage { public int CageStyle,PaintColor; }
public struct CostFlame
{
    public ulong flameSeed;
    public CostColor flameColor;
    public int flameRangeXMin,flameRangeXMax,flameRangeYMin,flameRangeYMax,flameCount;
    public float flameRangeMultX,flameRangeMultY;
    public CostTexture flameTexture;
}
