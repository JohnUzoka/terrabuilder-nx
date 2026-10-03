using System.Collections.Generic;

internal static class GameGuardTemplate
{
    public static Hint52Vector2 _NXHint52Measure(Hint52Font font, string text, Hint52Vector2 scale, float maxWidth)
    {
        // Keep the original string overload's parsing, including arbitrary handler callbacks.
        var snippets = Hint52Chat.ParseMessage(text, Hint52Color.White);
        // The original aggregate initializes Vector2 before the first snippet is evaluated.
        var zero = Hint52Vector2.Zero;
        if (_NXHint52CanSkip(font, snippets, scale, maxWidth)) return zero;
        return Hint52Chat.GetStringSize(font, snippets, scale, maxWidth);
    }

    private static bool _NXHint52CanSkip(Hint52Font font, List<Hint52Snippet> snippets, Hint52Vector2 scale, float maxWidth)
    {
        if (scale.X != 1f || scale.Y != 1f || maxWidth != -1f ||
            font == null || font.GetType() != typeof(Hint52Font) || snippets == null || snippets.Count > 256)
            return false;

        // Do not run virtual snippet methods or initialize numeric dependencies until the
        // whole population is known. In particular subclasses, item/achievement/plain
        // snippets and arbitrary replacement-handler outputs retain original layout.
        int total = 0;
        for (int i = 0; i < snippets.Count; i++)
        {
            var snippet = snippets[i];
            if (snippet == null) return false;
            var type = snippet.GetType();
            if (type != typeof(Hint52Snippet) && type != typeof(Hint52GlyphSnippet)) return false;
            var text = snippet.Text;
            if (text == null || text.Length > 4096 - total) return false;
            total += text.Length;
        }

        // Preserve the original first-use order: glyph scale at UniqueDraw, and language
        // before font wrapping/measurement. Glyph-only populations never touch language.
        for (int i = 0; i < snippets.Count; i++)
        {
            var snippet = snippets[i];
            if (snippet.GetType() == typeof(Hint52GlyphSnippet))
            {
                float glyphScale = Hint52GlyphHandler.GlyphsScale;
                if (!(glyphScale >= 0f && glyphScale <= 4f)) return false;
            }
            else
            {
                if (Hint52LanguageManager.Instance == null) return false;
                var culture = Hint52Language.ActiveCulture;
                if (culture == null || culture.CultureInfo == null) return false;
                if (!FontGuardTemplate._NXHint52MetricsSafe(font, snippet.Text!)) return false;
            }
        }
        return true;
    }
}
