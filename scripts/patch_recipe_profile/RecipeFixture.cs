using System.Globalization;

// Executable host boundaries, never emitted into Terraria. Recipe algorithms are cloned IL.
public static class RecipeFixture
{
    public static readonly List<string> Events = new();
    public static readonly Exception Failure = new InvalidOperationException("original recipe boundary failure");
    public static string? ThrowAt;
    public static Action<string>? Callback;
    public static RecipePlayer? ExpectedPlayer;
    public static RecipeItem[]? ExpectedInventory;
    public static bool AliasesIntact;
    public static void Effect(string value)
    {
        Events.Add(value); Callback?.Invoke(value);
        if (value == ThrowAt) throw Failure;
    }
    public static void Reset()
    {
        Events.Clear(); ThrowAt = null; Callback = null; AliasesIntact = true;
        RecipeMain.focusRecipe = 1; RecipeMain.numAvailableRecipes = 3;
        RecipeMain.availableRecipe = new[] { 8, 1, 9, 7, 6, 5 };
        RecipeMain.guideItem = new RecipeItem { type = 0, NameValue = "" };
        RecipeMain.LocalPlayerValue = new RecipePlayer { inventory = new[] { new RecipeItem { type = 11 }, new RecipeItem { type = 22 } } };
        RecipeMain.craftingUI = new RecipeCrafting(); RecipeCrafting.availableRecipeY = new[] { 0f, 10f, 20f, 30f, 40f, 50f };
        RecipeCrafting.Filter = null; RecipeNode.maxRecipes = 3; RecipeNode.maxRequirements = 3;
        RecipeMain.recipe = new[] { Node(1), Node(2), Node(3) };
        RecipeNode._ownedItems = new Dictionary<int, int> { [999] = 99 };
        ExpectedPlayer = RecipeMain.LocalPlayerValue; ExpectedInventory = ExpectedPlayer.inventory;
    }
    public static RecipeNode Node(int id) => new() { Id = id, createItem = new() { type = id + 100 }, requiredItemQuickLookup = new[] { new RecipeRequired { itemIdOrRecipeGroup = id + 10 }, new RecipeRequired(), new RecipeRequired() } };
    public static string Snapshot() => string.Join("|", string.Join(",", Events), RecipeMain.focusRecipe, RecipeMain.numAvailableRecipes,
        RecipeMain.availableRecipe == null ? "null" : string.Join(",", RecipeMain.availableRecipe), RecipeCrafting.availableRecipeY == null ? "null" : string.Join(",", RecipeCrafting.availableRecipeY.Select(x => x.ToString("R", CultureInfo.InvariantCulture))),
        RecipeNode._ownedItems == null ? "null" : string.Join(",", RecipeNode._ownedItems.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value)), AliasesIntact,
        RecipeMain.recipe == null ? "null" : string.Join(";", RecipeMain.recipe.Select(r => r?.requiredItemQuickLookup == null ? "null" : string.Join(",", r.requiredItemQuickLookup.Select(e => e.MatchesCalls)))));
}
public sealed class RecipeItem
{
    public int type;
    public string? NameValue;
    public bool IsAir { get { RecipeFixture.Effect("IsAir:" + type); return type == 0; } }
    public string? Name { get { RecipeFixture.Effect("Name:" + NameValue); return NameValue; } }
}
public sealed class RecipePlayer { public RecipeItem[]? inventory; }
public static class RecipeMain
{
    public static int focusRecipe, numAvailableRecipes;
    public static int[]? availableRecipe;
    public static RecipeItem? guideItem;
    public static RecipeNode[]? recipe;
    public static RecipeCrafting? craftingUI;
    public static RecipePlayer? LocalPlayerValue;
    public static RecipePlayer? LocalPlayer { get { RecipeFixture.Effect("LocalPlayer"); return LocalPlayerValue; } }
}
public sealed class RecipeCrafting
{
    public static float[]? availableRecipeY;
    public static RecipeFilter? Filter;
    public static RecipeFilter? RecipeFilterHack { get { RecipeFixture.Effect("Filter"); return Filter; } }
    public static Action<RecipeCrafting, int>? Reposition;
    public void VisuallyRepositionRecipes(int previousFocus)
    {
        RecipeFixture.Effect("Reposition:" + previousFocus);
        Reposition!(this, previousFocus);
    }
}
public sealed class RecipeFilter
{
    public readonly HashSet<int> Rejected = new();
    public bool Accepts(RecipeNode recipe) { RecipeFixture.Effect("Accepts:" + recipe.Id); return !Rejected.Contains(recipe.Id); }
}
public struct RecipeRequired
{
    public int itemIdOrRecipeGroup, MatchesCalls;
    public bool Matches(int type) { MatchesCalls++; RecipeFixture.Effect("Matches:" + itemIdOrRecipeGroup + ":" + type); return itemIdOrRecipeGroup == type; }
}
public sealed class RecipeNode
{
    public static int maxRecipes, maxRequirements;
    public static Dictionary<int, int>? _ownedItems;
    public RecipeItem? createItem;
    public RecipeRequired[]? requiredItemQuickLookup;
    public int Id;
    public bool Environment = true, Material = true;
    public bool PlayerMeetsEnvironmentConditions(RecipePlayer player, List<string>? reasons)
    {
        RecipeFixture.AliasesIntact &= ReferenceEquals(player, RecipeFixture.ExpectedPlayer) && reasons == null;
        RecipeFixture.Effect("Environment:" + Id); return Environment;
    }
    public static bool CollectedEnoughItemsToCraft(RecipeNode recipe) { RecipeFixture.Effect("Material:" + recipe.Id); return recipe.Material; }
    public static void CollectItems(RecipeItem[] inventory, int count)
    {
        RecipeFixture.AliasesIntact &= ReferenceEquals(inventory, RecipeFixture.ExpectedInventory);
        RecipeFixture.Effect("CollectItems:" + count + ":" + (_ownedItems!.Count));
        _ownedItems[11] = 4;
    }
    public static void CollectItemsFromChests(RecipePlayer player)
    {
        RecipeFixture.AliasesIntact &= ReferenceEquals(player, RecipeFixture.ExpectedPlayer);
        RecipeFixture.Effect("Chests"); _ownedItems![22] = 3;
    }
    public static void AddFakeCountsForItemGroups() { RecipeFixture.Effect("Groups"); _ownedItems![-1] = 7; }
}
public static class RecipeRequests
{
    public static void SubtractPendingRequests() { RecipeFixture.Effect("Requests"); RecipeNode._ownedItems![11]--; }
}
