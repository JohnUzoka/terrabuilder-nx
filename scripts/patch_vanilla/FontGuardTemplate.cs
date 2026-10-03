internal static class FontGuardTemplate
{
    public static bool _NXHint52MetricsSafe(Hint52Font font, string text)
    {
        if (font == null || font.GetType() != typeof(Hint52Font) || text == null || text.Length > 4096)
            return false;

        var characters = font._spriteCharacters;
        var fallback = font._defaultCharacterData;
        if (characters == null || fallback == null)
            return false;

        float spacing = font.CharacterSpacing;
        int lineSpacing = font.LineSpacing;
        if (!_NXHint52MetricInRange(spacing) || lineSpacing < 0 || lineSpacing > 1024)
            return false;

        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            // MeasureString ignores CR and handles LF without a character lookup.
            if (character == '\r' || character == '\n')
                continue;

            var data = character < characters.Length ? characters[character] : null;
            data ??= fallback;
            var kerning = data.Kerning;
            int height = data.Padding.Height;
            if (!_NXHint52MetricInRange(kerning.X) || !_NXHint52MetricInRange(kerning.Y) ||
                !_NXHint52MetricInRange(kerning.Z) || height < 0 || height > 1024)
                return false;
        }

        return true;
    }

    private static bool _NXHint52MetricInRange(float value) => value >= -1024f && value <= 1024f;
}
