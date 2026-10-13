using PenForLang.Ink;
using PenForLang.Platform.Windows.Native;

namespace PenForLang.Platform.Windows.Tests;

public class MouseMessageProcessorTests
{
    private readonly MouseMessageProcessor _processor = new();

    public MouseMessageProcessorTests()
    {
        _processor.SetSurface(new PenSurface(100, 50, 800, 600));
    }

    private bool Send(uint message, double screenX, double screenY, long timeUs, out PenSample sample, nint wParam = 0)
    {
        bool handled = _processor.TryProcess(message, wParam, screenX, screenY, timeUs, out sample, out bool has);
        Assert.Equal(handled, has);
        return handled;
    }

    [Fact]
    public void Left_button_drag_inside_gives_down_move_up_with_constant_pressure()
    {
        Assert.True(Send(NativeConstants.WmLButtonDown, 200, 150, 1_000, out PenSample down));
        Assert.True(Send(NativeConstants.WmMouseMove, 210, 150, 6_000, out PenSample move, (nint)NativeConstants.MkLButton));
        Assert.True(Send(NativeConstants.WmLButtonUp, 210, 150, 11_000, out PenSample up));

        Assert.Equal([PenPhase.Down, PenPhase.Move, PenPhase.Up], [down.Phase, move.Phase, up.Phase]);
        Assert.Equal(MouseMessageProcessor.ContactPressure, down.Pressure);
        Assert.Equal(MouseMessageProcessor.ContactPressure, move.Pressure);
        Assert.Equal(100.0, down.X);
        Assert.Equal(100.0, down.Y);
        Assert.Equal(110.0, move.X);
        Assert.Equal(MouseMessageProcessor.MousePointerId, down.PointerId);
        Assert.False(_processor.IsOwned);
    }

    [Fact]
    public void Move_without_the_button_is_hover_with_zero_pressure()
    {
        Assert.True(Send(NativeConstants.WmMouseMove, 300, 300, 1_000, out PenSample hover));

        Assert.Equal(PenPhase.Hover, hover.Phase);
        Assert.Equal(0f, hover.Pressure);
    }

    [Fact]
    public void Button_down_outside_the_canvas_is_not_handled()
    {
        Assert.False(Send(NativeConstants.WmLButtonDown, 20, 20, 1_000, out _));
        Assert.False(Send(NativeConstants.WmMouseMove, 300, 300, 2_000, out _, (nint)NativeConstants.MkLButton));
    }

    [Fact]
    public void Dragging_out_of_the_canvas_marks_points_clipped()
    {
        Send(NativeConstants.WmLButtonDown, 200, 150, 1_000, out _);

        Assert.True(Send(NativeConstants.WmMouseMove, 5000, 150, 2_000, out PenSample move, (nint)NativeConstants.MkLButton));
        Assert.True(move.IsClipped);
    }

    [Fact]
    public void Lost_capture_closes_the_open_stroke_without_marking_the_message_handled()
    {
        Send(NativeConstants.WmLButtonDown, 200, 150, 1_000, out _);

        bool handled = _processor.TryProcess(NativeConstants.WmCaptureChanged, 0, 0, 0, 9_000, out PenSample up, out bool has);

        Assert.False(handled);
        Assert.True(has);
        Assert.Equal(PenPhase.Up, up.Phase);
        Assert.Equal(9_000, up.TimeUs);
        Assert.False(_processor.IsOwned);
    }

    [Fact]
    public void Lost_capture_without_a_stroke_does_nothing()
    {
        Assert.False(_processor.TryProcess(NativeConstants.WmCaptureChanged, 0, 0, 0, 1_000, out _, out bool has));
        Assert.False(has);
    }

    [Fact]
    public void Without_a_surface_nothing_is_handled()
    {
        var processor = new MouseMessageProcessor();

        Assert.False(processor.TryProcess(NativeConstants.WmLButtonDown, 0, 200, 150, 1_000, out _, out bool has));
        Assert.False(has);
    }
}
