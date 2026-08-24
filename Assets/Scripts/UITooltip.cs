using UnityEngine;

// The hover-tooltip layer. Every explanatory sentence the rails used to print permanently now lives
// here, shown only while the cursor rests on the control it describes.
//
// Deliberately NOT built on GUI.tooltip. That mechanism only fires for controls drawn from a
// GUIContent carrying a tooltip, and every UITheme helper takes a bare string, so adopting it would
// mean changing all ~150 signatures (including the legacy Site tool's call sites) and would still
// hand back only a string, never the rect. The manual pattern below is what UITheme.StateRow already
// proves works in this exact nesting of BeginArea inside BeginArea inside BeginScrollView.
//
// The one thing that makes this simple: the tracker carries a STRING, never a rect. Hover() runs
// inside a layout area, where Event.current.mousePosition is area-local and the caller's rect is in
// the same space, so Contains is correct with no transform. Draw() runs after every EndArea, where
// mousePosition is top-level. Also correct. There is no coordinate conversion anywhere in this file,
// and that is the whole reason there is nothing here to get wrong.
public static class UITooltip
{
    /// <summary>How long the cursor must rest before a tip appears.</summary>
    public const float Delay = 0.45f;

    // What is hovered THIS repaint, versus what the delay timer is currently running for. Two fields,
    // because moving between two controls has to restart the timer rather than swap the text instantly.
    private static string _text;
    private static string _armed;
    private static float _armedAt;

    // A live readout claimed by whatever is being dragged this frame. It is not a tooltip and does not
    // share their delay or their suppression: a tip explains a control you are resting on, this one
    // reports the value of a control you are actively moving, and it has to appear instantly.
    private static string _pinText;
    private static Vector2 _pinAt;

    /// <summary>First line of OnGUI. Clears the frame's hover so a control that is no longer under the
    /// cursor stops claiming it.</summary>
    public static void BeginFrame()
    {
        // Only on Repaint: Hover() only records on Repaint, so clearing on every event would wipe the
        // record before Draw() (also Repaint-only) ever saw it.
        if (Event.current != null && Event.current.type == EventType.Repaint)
        {
            _text = null;
            _pinText = null;
        }
    }

    /// <summary>
    /// Pins a value chip at <paramref name="at"/> for this frame: the live feedback a drag-scrubbed
    /// field shows while the pointer is moving it.
    ///
    /// It lives here rather than in UITheme for the reason the whole file does: this has to be drawn
    /// after every EndArea or it is clipped to the rail it came from, and UITooltip already owns that
    /// deferral and already knows how to reach OverlayDraw, which UITheme, shared with the Site tool,
    /// deliberately does not.
    /// </summary>
    public static void Pin(Vector2 at, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        _pinText = text;
        _pinAt   = at;
    }

    /// <summary>Claims the tooltip for this frame if the cursor is inside <paramref name="rect"/>.</summary>
    public static void Hover(Rect rect, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (!rect.Contains(Event.current.mousePosition)) return;
        _text = text;
    }

    /// <summary>Last line of OnGUI, after every GUILayout.EndArea: a tip drawn inside an area would be
    /// clipped to it, and a tip drawn before the rails would be painted over by them.</summary>
    public static void Draw()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        // Before the hotControl test below, not after: a pin is claimed BY the drag that test exists
        // to suppress tips during, so deferring to it would hide the one thing that must be visible.
        if (!string.IsNullOrEmpty(_pinText)) OverlayDraw.Readout(_pinAt, _pinText);

        // Nothing hovered, or a drag is in progress. A tip that pops up mid-slider-drag sits under the
        // cursor obscuring the very value being dragged.
        if (string.IsNullOrEmpty(_text) || GUIUtility.hotControl != 0)
        {
            _armed = null;
            return;
        }

        // Moved to a different control: restart the wait rather than swapping text instantly, which
        // would strobe tips at every control the cursor crossed on its way somewhere else.
        if (_text != _armed)
        {
            _armed = _text;
            _armedAt = Time.realtimeSinceStartup;
            return;
        }

        if (Time.realtimeSinceStartup - _armedAt < Delay) return;

        OverlayDraw.Tip(Event.current.mousePosition, _text);
    }
}
