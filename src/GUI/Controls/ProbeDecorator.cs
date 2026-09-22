using System.Windows;
using System.Windows.Controls;

namespace DivinityModManager.Controls;

/// <summary>
/// TEMPORARY. A transparent wrapper that times its child's Measure and Arrange under
/// <see cref="ProbeLabel"/>. TextBlock seals MeasureOverride, ArrangeOverride and OnRender, so a
/// subclass cannot time them; wrapping is the only way to time one TextBlock on its own.
/// Arrange includes the child's OnRender, which WPF runs inside Arrange when the content changed.
/// Removed with the other instrumentation.
/// </summary>
public class ProbeDecorator : Decorator
{
	public string ProbeLabel { get; set; } = "ProbeDecorator";

	protected override Size MeasureOverride(Size constraint)
	{
		var probe = Util.PerfProbe.Begin();
		try
		{
			return base.MeasureOverride(constraint);
		}
		finally
		{
			Util.PerfProbe.End($"{ProbeLabel} Measure", probe);
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
			Util.PerfProbe.End($"{ProbeLabel} Arrange", probe);
		}
	}
}
