using System;
using SafeCodexQuotaWidget;

internal static class PointerGestureTests
{
    private const int HoldMilliseconds = 220;
    private const double MoveTolerance = 4;

    private static int Main()
    {
        try
        {
            ShortTapExpandsCollapsedWidget();
            ShortTapCollapsesExpandedWidget();
            SmallPointerJitterRemainsATap();
            MovementStartsDragAndNeverToggles();
            HoldBoundaryStartsDragAndNeverToggles();
            ExistingDragNeverToggles();
            ExpandedFrameAloneDoesNotHover();
            ExpandedGaugeActivatesHover();
            CollapsedVisibleSurfaceActivatesHover();
            Console.WriteLine("Pointer gesture tests passed: 9");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void ShortTapExpandsCollapsedWidget()
    {
        Assert(!PointerGestureRules.ShouldStartDragOnRelease(false, 80, 0, HoldMilliseconds, MoveTolerance),
            "A short stationary tap must not start dragging.");
        Assert(PointerGestureRules.GetTapAction(false, false, 80, 0, HoldMilliseconds, MoveTolerance) == PointerTapAction.Expand,
            "A short tap should expand a collapsed widget.");
    }

    private static void ShortTapCollapsesExpandedWidget()
    {
        Assert(PointerGestureRules.GetTapAction(false, true, 80, 0, HoldMilliseconds, MoveTolerance) == PointerTapAction.Collapse,
            "A short tap should collapse an expanded widget.");
    }

    private static void SmallPointerJitterRemainsATap()
    {
        Assert(PointerGestureRules.GetTapAction(false, true, 120, MoveTolerance, HoldMilliseconds, MoveTolerance) == PointerTapAction.Collapse,
            "Movement exactly at the tolerance should remain a tap.");
    }

    private static void MovementStartsDragAndNeverToggles()
    {
        double moved = MoveTolerance + 0.01;
        Assert(PointerGestureRules.ShouldStartDragOnRelease(false, 80, moved, HoldMilliseconds, MoveTolerance),
            "Movement above the tolerance should start dragging.");
        Assert(PointerGestureRules.GetTapAction(true, true, 80, moved, HoldMilliseconds, MoveTolerance) == PointerTapAction.None,
            "A movement drag must never collapse the widget.");
    }

    private static void HoldBoundaryStartsDragAndNeverToggles()
    {
        Assert(PointerGestureRules.ShouldStartDragOnRelease(false, HoldMilliseconds, 0, HoldMilliseconds, MoveTolerance),
            "The exact hold boundary should be treated as a drag gesture.");
        Assert(PointerGestureRules.GetTapAction(true, true, HoldMilliseconds, 0, HoldMilliseconds, MoveTolerance) == PointerTapAction.None,
            "A long hold must never collapse the widget.");
    }

    private static void ExistingDragNeverToggles()
    {
        Assert(!PointerGestureRules.ShouldStartDragOnRelease(true, 400, 30, HoldMilliseconds, MoveTolerance),
            "An existing drag should not be started a second time.");
        Assert(PointerGestureRules.GetTapAction(true, false, 40, 0, HoldMilliseconds, MoveTolerance) == PointerTapAction.None,
            "An existing drag must not expand the widget on release.");
    }

    private static void ExpandedFrameAloneDoesNotHover()
    {
        Assert(!HoverActivationRules.IsActive(true, false, true),
            "An expanded panel must stay static when only the rectangular frame is hovered.");
    }

    private static void ExpandedGaugeActivatesHover()
    {
        Assert(HoverActivationRules.IsActive(true, true, true),
            "An expanded panel should animate while the quota circle is hovered.");
    }

    private static void CollapsedVisibleSurfaceActivatesHover()
    {
        Assert(HoverActivationRules.IsActive(false, true, false),
            "The collapsed quota circle should keep its hover animation.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
