using System.Windows;
using System.Windows.Controls;

namespace DivinityModManager.Controls;

/// <summary>
/// TEMPORARY. The default VirtualizingStackPanel with its layout timed per owning list. Measure is
/// where scrolling recycles rows and re-binds them to new mods; Arrange positions the rows and
/// arranges any cell whose content changed. Removed with the other instrumentation.
/// </summary>
public class ProbeVirtualizingStackPanel : VirtualizingStackPanel
{
	private string _label;
	private string Label => _label ??= ItemsControl.GetItemsOwner(this)?.Name is string name && name.Length > 0 ? name : "<unnamed>";

	protected override Size MeasureOverride(Size constraint)
	{
		var probe = Util.PerfProbe.Begin();
		try
		{
			return base.MeasureOverride(constraint);
		}
		finally
		{
			Util.PerfProbe.End($"VSP Measure {Label}", probe);
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
			Util.PerfProbe.End($"VSP Arrange {Label}", probe);
		}
	}
}
