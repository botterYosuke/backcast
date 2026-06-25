// ThemedInputFieldBuilder.cs — #137 S1/S3 (findings 0107 D5): the ONE place the Settings input面 widget is
// built, so every Settings card's input reads identically — a sunk fill (surface_background), a visible
// border (Outline=border), body text (text), and an italic placeholder (text_placeholder). Both the Scenario
// tile fields (ScenarioStartupTile.MakeField) and the Data root field (SettingsDataSectionView) mint their
// fields here, so a future #52 spacing/role-token pass — or any input-role change — touches a single builder
// and the two cards can't drift apart. The caller anchors the returned `field`'s RectTransform and registers
// the graphics for its own ApplyTheme (or calls ThemedInputField.ApplyTheme to repaint them all at once).

using UnityEngine;
using UnityEngine.UI;

// The built field + its themed graphics, so the owner can repaint on a Dark/Light switch.
public struct ThemedInputField
{
    public InputField field;
    public Image fill;
    public Outline border;
    public Text body;
    public Text placeholder;

    public void ApplyTheme()
    {
        var c = ThemeService.Current.colors;
        if (fill != null) fill.color = c.surface_background;
        if (border != null) border.effectColor = c.border;
        if (body != null) body.color = c.text;
        if (placeholder != null) placeholder.color = c.text_placeholder;
    }
}

public static class ThemedInputFieldBuilder
{
    // Build a bordered + placeholdered Settings input field under `parent` (the caller anchors
    // `result.field`'s RectTransform and wires onValueChanged/onEndEdit). `name` lets a caller pin a
    // findable GameObject name (e.g. "duckdb_field"); defaults to "field".
    public static ThemedInputField Build(RectTransform parent, Font font, string placeholder, string name = "field")
    {
        var c = ThemeService.Current.colors;
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField), typeof(Outline));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);

        var fill = go.GetComponent<Image>();
        fill.color = c.surface_background;                 // sunk input面 (D5)
        var border = go.GetComponent<Outline>();           // the visible frame (D5)
        border.effectColor = c.border;
        border.effectDistance = new Vector2(1f, 1f);

        var body = StretchText(rt, font, "text", c.text, italic: false);
        var ph = StretchText(rt, font, "placeholder", c.text_placeholder, italic: true);
        ph.text = placeholder;

        var field = go.GetComponent<InputField>();
        field.textComponent = body;
        field.placeholder = ph;

        return new ThemedInputField { field = field, fill = fill, border = border, body = body, placeholder = ph };
    }

    static Text StretchText(RectTransform parent, Font font, string name, Color color, bool italic)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(5f, 5f); rt.offsetMax = new Vector2(-5f, -5f);
        var t = go.GetComponent<Text>();
        t.font = font; t.color = color; t.fontSize = 11; t.alignment = TextAnchor.MiddleLeft;
        t.supportRichText = false;
        if (italic) t.fontStyle = FontStyle.Italic;
        return t;
    }
}
