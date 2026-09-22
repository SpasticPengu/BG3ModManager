using DivinityModManager.Util.ScreenReader;

using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DivinityModManager.Controls;

public class ModEntryGrid : Grid
{
	public ModEntryGrid() : base() { }

	protected override AutomationPeer OnCreateAutomationPeer()
	{
		Util.PerfProbe.Count("AutomationPeer created (ModEntryGrid)");
		return new ModEntryGridAutomationPeer(this);
	}
}
