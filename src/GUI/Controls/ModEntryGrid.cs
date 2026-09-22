using DivinityModManager.Util.ScreenReader;

using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DivinityModManager.Controls;

public class ModEntryGrid : Grid
{
	public ModEntryGrid() : base() { }

	protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
	{
		var probe = Util.PerfProbe.Begin();
		try
		{
			return base.MeasureOverride(constraint);
		}
		finally
		{
			Util.PerfProbe.End("ModEntryGrid.Measure (name cell)", probe);
		}
	}

	protected override System.Windows.Size ArrangeOverride(System.Windows.Size arrangeSize)
	{
		var probe = Util.PerfProbe.Begin();
		try
		{
			return base.ArrangeOverride(arrangeSize);
		}
		finally
		{
			Util.PerfProbe.End("ModEntryGrid.Arrange (name cell)", probe);
		}
	}

	protected override AutomationPeer OnCreateAutomationPeer()
	{
		Util.PerfProbe.Count("AutomationPeer created (ModEntryGrid)");
		return new ModEntryGridAutomationPeer(this);
	}
}
