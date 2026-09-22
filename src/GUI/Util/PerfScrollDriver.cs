using DivinityModManager.Views;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace DivinityModManager.Util;

/// <summary>
/// TEMPORARY. Runs a fixed, repeatable scroll workload over the active mod list so layout timings
/// can be compared run to run without anyone touching the mouse. Enabled by an extension-less
/// "perfscroll" file next to the exe (the "perfprobe" file must also exist). Once loading has
/// finished and the UI has been quiet for a few seconds, it scrolls the whole list top to bottom
/// and back <see cref="RoundTrips"/> times, one step per rendered frame - roughly what dragging
/// the scrollbar thumb does - then writes a single probe summary and closes the app.
/// Removed with the other instrumentation.
/// </summary>
public static class PerfScrollDriver
{
	private const int RoundTrips = 3;
	private const int QuietTicksBeforeStart = 8;

	// "stepN" in the perfprobe file overrides this. 5 rows/frame is a brisk wheel scroll; 25+ is
	// closer to dragging the scrollbar thumb, where most realized rows are re-bound every frame.
	private static double ItemsPerFrame = 5;

	private static int? FlagNumber(string prefix)
	{
		foreach (var flag in PerfProbe.Variant.Split(','))
		{
			if (flag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(flag.AsSpan(prefix.Length), out var n)) return n;
		}
		return null;
	}

	private static DispatcherTimer readyTimer;
	private static int quietTicks;
	private static ScrollViewer scrollViewer;
	private static VirtualizingStackPanel panel;
	private static double offset;
	private static int direction = 1;
	private static int legsDone;
	private static long frames;
	private static long startStamp;

	public static void Start()
	{
		if (!PerfProbe.Enabled) return;
		readyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
		readyTimer.Tick += OnReadyTick;
		readyTimer.Start();
		DivinityApp.Log("[PerfScroll] waiting for the mod list to finish loading.");
	}

	private static void OnReadyTick(object sender, EventArgs e)
	{
		var window = MainWindow.Self;
		var vm = window?.ViewModel;
		var list = window?.MainView?.ModLayout?.ActiveModsView;
		var ready = vm != null && vm.IsInitialized && !vm.IsRefreshing && !vm.IsLoadingOrder && !vm.MainProgressIsActive
			&& list != null && list.Items.Count > 100;
		quietTicks = ready ? quietTicks + 1 : 0;
		if (quietTicks < QuietTicksBeforeStart) return;

		readyTimer.Stop();
		scrollViewer = list.FindVisualChildren<ScrollViewer>().FirstOrDefault();
		if (scrollViewer == null)
		{
			DivinityApp.Log("[PerfScroll] no ScrollViewer found on the active list - aborting.");
			return;
		}

		if (FlagNumber("step") is int step) ItemsPerFrame = step;
		if (FlagNumber("cache") is int cache)
		{
			VirtualizingPanel.SetCacheLengthUnit(list, VirtualizationCacheLengthUnit.Item);
			VirtualizingPanel.SetCacheLength(list, new VirtualizationCacheLength(cache));
		}
		scrollViewer.ScrollToVerticalOffset(0);
		list.UpdateLayout();
		offset = 0;

		// Discard everything recorded during startup, so the summary holds only the scroll workload.
		PerfProbe.Dump("startup (discarded)");
		PerfProbe.PeriodicDumps = false;
		panel = list.FindVisualChildren<VirtualizingStackPanel>().FirstOrDefault();
		DivinityApp.Log($"[PerfScroll] start: {list.Items.Count} rows, scrollable {scrollViewer.ScrollableHeight}, "
			+ $"viewport {scrollViewer.ViewportHeight} rows, realized {panel?.Children.Count}, "
			+ $"cache {VirtualizingPanel.GetCacheLength(list)} {VirtualizingPanel.GetCacheLengthUnit(list)}, "
			+ $"{ItemsPerFrame} rows/frame, {RoundTrips} round trips, variant {PerfProbe.Variant}");
		LogNameCellRenderInputs(list);
		startStamp = Stopwatch.GetTimestamp();
		CompositionTarget.Rendering += OnRendering;
	}

	/// <summary>
	/// Logs the font and colour the name element resolves to, next to a plain TextBlock in the same
	/// row, so a change of element type can be shown to draw with identical inputs.
	/// </summary>
	private static void LogNameCellRenderInputs(ListView list)
	{
		var grid = list.FindVisualChildren<Controls.ModEntryGrid>().FirstOrDefault();
		var name = grid == null ? null : ElementHelper.FindChild<FrameworkElement>(grid, "ModNameText");
		DependencyObject row = grid;
		while (row != null && row is not GridViewRowPresenter) row = VisualTreeHelper.GetParent(row);
		var sibling = row?.FindVisualChildren<TextBlock>().FirstOrDefault(t => !IsInside(t, grid));
		static string Describe(DependencyObject e) => e == null ? "<none>"
			: $"{e.GetType().Name}: {TextElement.GetFontFamily(e)} {TextElement.GetFontSize(e)} {TextElement.GetFontWeight(e)} {TextElement.GetFontStyle(e)} "
				+ $"{TextElement.GetFontStretch(e)} fg={TextElement.GetForeground(e)} (frozen {TextElement.GetForeground(e)?.IsFrozen}) mode={TextOptions.GetTextFormattingMode(e)} lang={e.GetValue(FrameworkElement.LanguageProperty)}";
		DivinityApp.Log($"[PerfScroll] name cell renders with  {Describe(name)}");
		DivinityApp.Log($"[PerfScroll] sibling cell renders with {Describe(sibling)}");
	}

	private static bool IsInside(DependencyObject element, DependencyObject ancestor)
	{
		for (var e = element; e != null; e = VisualTreeHelper.GetParent(e))
		{
			if (e == ancestor) return true;
		}
		return false;
	}

	private static void OnRendering(object sender, EventArgs e)
	{
		frames++;
		var max = scrollViewer.ScrollableHeight;
		offset += direction * ItemsPerFrame;
		if (offset >= max)
		{
			offset = max;
			direction = -1;
			legsDone++;
		}
		else if (offset <= 0)
		{
			offset = 0;
			direction = 1;
			legsDone++;
		}
		scrollViewer.ScrollToVerticalOffset(offset);

		if (legsDone >= RoundTrips * 2)
		{
			CompositionTarget.Rendering -= OnRendering;
			var seconds = (Stopwatch.GetTimestamp() - startStamp) / (double)Stopwatch.Frequency;
			DivinityApp.Log($"[PerfScroll] done: {frames} steps in {seconds:F2}s ({frames / seconds:F1} steps/s), realized at end {panel?.Children.Count}");
			PerfProbe.Dump("scroll");
			Trace.Flush();
			Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Application.Current.Shutdown());
		}
	}
}
