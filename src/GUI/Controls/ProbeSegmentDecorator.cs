using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace DivinityModManager.Controls;

/// <summary>
/// TEMPORARY. Splits one child Arrange into the windows UIElement.Arrange spends time in, using
/// timestamps the child (ProbeCachedTextBlock) records from inside its overrides:
/// pre   = start of Arrange to ArrangeOverride (accessibility ancestor walk, measure-during-arrange, ArrangeCore)
/// mid   = ArrangeOverride end to OnRender start (layout clip, size-changed bookkeeping, RenderOpen)
/// post  = OnRender end to end of Arrange (drawing-context Close: swapping the render data, releasing the old one)
/// Diagnostic builds only.
/// </summary>
public class ProbeSegmentDecorator : Decorator
{
	internal static long ArrangeOverrideStart, ArrangeOverrideEnd, RenderStart, RenderEnd, MeasureTicks;
	internal static bool Active;

	protected override Size ArrangeOverride(Size arrangeSize)
	{
		ArrangeOverrideStart = ArrangeOverrideEnd = RenderStart = RenderEnd = MeasureTicks = 0;
		Active = true;
		var start = Stopwatch.GetTimestamp();
		try
		{
			return base.ArrangeOverride(arrangeSize);
		}
		finally
		{
			var end = Stopwatch.GetTimestamp();
			Active = false;
			Util.PerfProbe.Record("  seg: whole child Arrange", end - start);
			if (MeasureTicks > 0) Util.PerfProbe.Record("  seg: measure during arrange", MeasureTicks);
			if (ArrangeOverrideStart > 0)
			{
				Util.PerfProbe.Record("  seg: pre (walk/measure/ArrangeCore)", ArrangeOverrideStart - start);
				if (RenderStart > 0)
				{
					Util.PerfProbe.Record("  seg: mid (clip/RenderOpen)", RenderStart - ArrangeOverrideEnd);
					Util.PerfProbe.Record("  seg: OnRender", RenderEnd - RenderStart);
					Util.PerfProbe.Record("  seg: post (Close/render data swap)", end - RenderEnd);
				}
				else
				{
					Util.PerfProbe.Record("  seg: after override, no render", end - ArrangeOverrideEnd);
				}
			}
			else
			{
				Util.PerfProbe.Count("  seg: child arrange skipped (clean)");
			}
		}
	}
}
