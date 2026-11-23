using System.ComponentModel;

namespace DivinityModManager.Models;

public class WindowSettings
{
	public bool Maximized { get; set; }

	[DefaultValue(0)]
	public int Screen { get; set; }

	[DefaultValue(-1)]
	public double X { get; set; }

	[DefaultValue(-1)]
	public double Y { get; set; }

	[DefaultValue(-1)]
	public double Width { get; set; }

	[DefaultValue(-1)]
	public double Height { get; set; }

	public WindowSettings()
	{
		Maximized = false;
		X = -1;
		Y = -1;
		Width = -1;
		Height = -1;
		Screen = 0;
	}

	public void SetFrom(WindowSettings from)
	{
		if (from == null) return;
		Maximized = from.Maximized;
		Screen = from.Screen;
		X = from.X;
		Y = from.Y;
		Width = from.Width;
		Height = from.Height;
	}
}
