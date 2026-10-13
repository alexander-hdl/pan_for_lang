namespace PenForLang.Platform.Windows.Tests;

public class WindowInkSuppressorTests
{
    [Fact]
    public void Disable_mask_combines_the_documented_tablet_constants()
    {
        // tpcshrd.h / WM_TABLET_QUERYSYSTEMGESTURESTATUS: значения не выдуманы, а сверены с документацией.
        Assert.Equal(0x00000001u, WindowInkSuppressor.DisablePressAndHold);
        Assert.Equal(0x00000008u, WindowInkSuppressor.DisablePenTapFeedback);
        Assert.Equal(0x00000010u, WindowInkSuppressor.DisablePenBarrelFeedback);
        Assert.Equal(0x00010000u, WindowInkSuppressor.DisableFlicks);
        Assert.Equal(0x00010019u, WindowInkSuppressor.DisableMask);
        Assert.Equal("MicrosoftTabletPenServiceProperty", WindowInkSuppressor.PenServicePropertyName);
    }

    [Fact]
    public void Pen_feedback_types_match_the_feedback_type_enumeration()
    {
        Assert.Equal(
            [
                ("FEEDBACK_PEN_BARRELVISUALIZATION", 2u),
                ("FEEDBACK_PEN_TAP", 3u),
                ("FEEDBACK_PEN_DOUBLETAP", 4u),
                ("FEEDBACK_PEN_PRESSANDHOLD", 5u),
                ("FEEDBACK_PEN_RIGHTTAP", 6u),
            ],
            WindowInkSuppressor.PenFeedbackTypes);
    }

    [Fact]
    public void Query_system_gesture_status_is_answered_with_the_disable_mask()
    {
        // WM_TABLET_DEFBASE (0x02C0) + 12.
        Assert.True(WindowInkSuppressor.TryGetMessageResult(0x02CC, out nint result));
        Assert.Equal((nint)WindowInkSuppressor.DisableMask, result);
    }

    [Fact]
    public void Other_messages_are_not_answered()
    {
        Assert.False(WindowInkSuppressor.TryGetMessageResult(0x0246, out nint result));
        Assert.Equal(0, result);
    }

    [Fact]
    public void Apply_on_an_invalid_window_reports_errors_instead_of_throwing()
    {
        InkSuppressionResult result = WindowInkSuppressor.Apply(hwnd: 0x7FFF_FFF0);

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Errors);
    }
}
