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
			Util.PerfProbe.SampleStack("ModEntryGrid.Arrange entry path", 1500);
		}
	}

	private bool _variantApplied;

	protected override void OnInitialized(EventArgs e)
	{
		base.OnInitialized(e);
		ApplyProbeVariant();
	}

	/// <summary>
	/// TEMPORARY A/B switches for the name TextBlock, driven by the contents of the "perfprobe" file.
	/// Local values outrank template values, so these override ModNameTemplate without editing it.
	/// </summary>
	private void ApplyProbeVariant()
	{
		if (_variantApplied || !Util.PerfProbe.Enabled) return;
		if (Util.ElementHelper.FindChild<TextBlock>(this, "ModNameText") is not TextBlock tb) return;
		_variantApplied = true;

		if (Util.PerfProbe.Has("notooltip")) tb.SetValue(System.Windows.FrameworkElement.ToolTipProperty, null);
		if (Util.PerfProbe.Has("notag")) tb.SetValue(System.Windows.FrameworkElement.TagProperty, null);
		if (Util.PerfProbe.Has("display")) System.Windows.Media.TextOptions.SetTextFormattingMode(tb, System.Windows.Media.TextFormattingMode.Display);
		if (Util.PerfProbe.Has("left")) tb.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
		if (Util.PerfProbe.Has("uuidtext")) tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("UUID"));
		if (Util.PerfProbe.Has("notext")) tb.Visibility = System.Windows.Visibility.Collapsed;
		Util.PerfProbe.Count("name TextBlock variant applied");
	}

	protected override AutomationPeer OnCreateAutomationPeer()
	{
		Util.PerfProbe.Count("AutomationPeer created (ModEntryGrid)");
		return new ModEntryGridAutomationPeer(this);
	}
}
