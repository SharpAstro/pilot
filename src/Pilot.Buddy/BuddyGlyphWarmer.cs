using System.Text;

namespace Pilot.Buddy;

/// <summary>The part of the GPU's font atlas the warmer needs: rasterise one glyph now, so the next frame's upload carries it.</summary>
public interface IBuddyAtlas
{
    /// <summary>Rasterises <paramref name="glyph"/> (and queues its upload for the next frame's flush) if the atlas has not got it.</summary>
    void PreWarm(Rune glyph);
}

/// <summary>
/// Why the bubble's text built up glyph by glyph (#1381 section 4), and the fix. SdlVulkan.Renderer's atlas rasterises a glyph on first use but the
/// glyph is "unflushed" until the next frame's command buffer uploads its page; the draw skips an unflushed glyph and reports <c>FontAtlasDirty</c>,
/// which its own event loop answers with another frame. The buddy's loop answers only to its frame policy, which asked for no frame when the atlas
/// changed: so the first presented frame after a wake held the bubble's box and none of its text (every glyph new to the atlas was skipped), and the
/// text came in with whichever frame the policy asked for next (a breath, a hover), the glyphs of that frame's own new letters again one frame late.
/// A first-time effect per glyph, never again for a glyph the atlas holds. The fix is to know the glyphs before presenting: the CPU pass that lays
/// the scene out also reports every text it draws, and <see cref="Warm"/> has the atlas rasterise the new glyphs before the GPU frame begins, so that
/// frame's flush uploads them and its draw finds them. <see cref="BuddyFramePolicy.AtlasSettled"/> is the backstop for anything this misses (the host makes its renderer, and so its atlas, once and never drops it, so there is nothing to forget and no reset).
/// </summary>
public sealed class BuddyGlyphWarmer(IBuddyAtlas atlas)
{
    private readonly HashSet<Rune> _known = [];
    private readonly List<Rune> _pending = [];

    /// <summary>How many different glyphs have been warmed.</summary>
    public int Warmed => _known.Count;

    /// <summary>Notes the glyphs of a text the scene is about to draw.</summary>
    public void Collect(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && _known.Add(rune))
            {
                _pending.Add(rune);
            }
        }
    }

    /// <summary>Has the atlas rasterise every glyph collected since the last call; returns how many were new.</summary>
    public int Warm()
    {
        var count = _pending.Count;
        foreach (var rune in _pending)
        {
            atlas.PreWarm(rune);
        }
        _pending.Clear();
        return count;
    }
}
