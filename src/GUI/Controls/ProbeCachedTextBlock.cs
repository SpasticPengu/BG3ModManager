using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace DivinityModManager.Controls;

/// <summary>
/// TEMPORARY. CachedTextBlock that stamps when its overrides run, so ProbeSegmentDecorator can
/// split an in-app Arrange into the windows around them. Diagnostic builds only.
/// </summary>
public class ProbeCachedTextBlock : CachedTextBlock
{
	protected override Size MeasureOverride(Size availableSize)
	{
		var start = Stopwatch.GetTimestamp();
		try
		{
			return base.MeasureOverride(availableSize);
		}
		finally
		{
			var elapsed = Stopwatch.GetTimestamp() - start;
			Util.PerfProbe.Record("    name MeasureOverride (any caller)", elapsed);
			if (ProbeSegmentDecorator.Active) ProbeSegmentDecorator.MeasureTicks += elapsed;
		}
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		if (ProbeSegmentDecorator.Active) ProbeSegmentDecorator.ArrangeOverrideStart = Stopwatch.GetTimestamp();
		try
		{
			return base.ArrangeOverride(finalSize);
		}
		finally
		{
			if (ProbeSegmentDecorator.Active) ProbeSegmentDecorator.ArrangeOverrideEnd = Stopwatch.GetTimestamp();
		}
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		if (ProbeSegmentDecorator.Active) ProbeSegmentDecorator.RenderStart = Stopwatch.GetTimestamp();
		try
		{
			base.OnRender(drawingContext);
		}
		finally
		{
			if (ProbeSegmentDecorator.Active) ProbeSegmentDecorator.RenderEnd = Stopwatch.GetTimestamp();
		}
	}
}
