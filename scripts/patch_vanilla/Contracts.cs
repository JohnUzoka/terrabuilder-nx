// Patch-time views map to pinned game/ReLogic/FNA definitions; no view type ships.
internal struct Hint52Vector2 { internal float X, Y; internal static Hint52Vector2 Zero { get; } }
internal struct Hint52Vector3 { internal float X, Y, Z; }
internal struct Hint52Rectangle { internal int Height; }
internal struct Hint52Color { internal static Hint52Color White { get; } }
internal sealed class Hint52Character
{
    internal Hint52Vector3 Kerning;
    internal Hint52Rectangle Padding;
}
internal class Hint52Font
{
    internal Hint52Character[]? _spriteCharacters;
    internal Hint52Character? _defaultCharacterData;
    internal float CharacterSpacing { get; }
    internal int LineSpacing { get; }
}
internal class Hint52Snippet { internal string? Text; }
internal sealed class Hint52GlyphSnippet : Hint52Snippet { }
internal static class Hint52GlyphHandler { internal static float GlyphsScale; }
internal sealed class Hint52Culture { internal System.Globalization.CultureInfo? CultureInfo; }
internal sealed class Hint52LanguageManager { internal static Hint52LanguageManager? Instance; }
internal static class Hint52Language { internal static Hint52Culture? ActiveCulture { get; } }
internal static class Hint52Chat
{
    internal static System.Collections.Generic.List<Hint52Snippet> ParseMessage(string text, Hint52Color color) => throw new System.InvalidOperationException("patch-time view only");
    internal static Hint52Vector2 GetStringSize(Hint52Font font, System.Collections.Generic.IEnumerable<Hint52Snippet> snippets, Hint52Vector2 scale, float maximum) => throw new System.InvalidOperationException("patch-time view only");
}
