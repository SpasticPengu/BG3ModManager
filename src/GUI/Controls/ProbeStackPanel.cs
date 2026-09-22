using System.Windows;
using System.Windows.Controls;

namespace DivinityModManager.Controls;

/// <summary>
/// TEMPORARY. A StackPanel that times its own layout, used to split the ~219us spent arranging a
/// mod row's name cell between the icon strip and the name TextBlock. Renders identically to a
/// plain StackPanel, so the UI is unchanged while measuring. Removed with the other instrumentation.
/// </summary>
public class ProbeStackPanel : StackPanel
{
	protected override Size MeasureOverride(Size constraint)
	{
		var probe = Util.PerfProbe.Begin();
		try
		{
			return base.MeasureOverride(constraint);
		}
		finally
		{
			Util.PerfProbe.End("  -> icon strip Measure", probe);
		}
	}

	protected override Size ArrangeOverride(Size arrangeSize)
	{
		var probe = Util.PerfProbe.Begin();
		try
		{
			return base.ArrangeOverride(arrangeSize);
		}
		finally
		{
			Util.PerfProbe.End("  -> icon strip Arrange", probe);
		}
	}
}
