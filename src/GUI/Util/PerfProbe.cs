using System.Diagnostics;
using System.Text;
using System.Windows.Media;

namespace DivinityModManager.Util;

/// <summary>
/// TEMPORARY instrumentation for the Stage 6 list-render investigation.
/// Measures frame pacing plus hit counts and wall time on suspected hot paths,
/// and dumps a summary to the log. Not intended to ship - remove before any PR.
/// </summary>
public static class PerfProbe
{
	private static readonly object sync = new object();
	private static readonly Dictionary<string, long> counts = new Dictionary<string, long>();
	private static readonly Dictionary<string, long> ticks = new Dictionary<string, long>();
	private static readonly List<double> frameGaps = new List<double>();

	private static bool enabled;
	private static long lastFrameStamp;
	private static long lastDumpStamp;

	private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

	public static bool Enabled => enabled;

	public static void Start()
	{
		if (enabled) return;
		enabled = true;
		lastFrameStamp = 0;
		lastDumpStamp = Stopwatch.GetTimestamp();
		CompositionTarget.Rendering += OnRendering;
		DivinityApp.Log("[PerfProbe] active - scroll the list, then close the app for a summary.");
	}

	public static void Stop()
	{
		if (!enabled) return;
		CompositionTarget.Rendering -= OnRendering;
		enabled = false;
		Dump("final");
	}

	/// <summary>Returns a timestamp to pass to <see cref="End"/>, or 0 when disabled.</summary>
	public static long Begin()
	{
		return enabled ? Stopwatch.GetTimestamp() : 0L;
	}

	public static void End(string name, long start)
	{
		if (!enabled || start == 0L) return;
		var elapsed = Stopwatch.GetTimestamp() - start;
		lock (sync)
		{
			counts.TryGetValue(name, out var c);
			counts[name] = c + 1;
			ticks.TryGetValue(name, out var t);
			ticks[name] = t + elapsed;
		}
	}

	public static void Count(string name)
	{
		if (!enabled) return;
		lock (sync)
		{
			counts.TryGetValue(name, out var c);
			counts[name] = c + 1;
		}
	}

	private static void OnRendering(object sender, EventArgs e)
	{
		var now = Stopwatch.GetTimestamp();
		if (lastFrameStamp != 0)
		{
			var gap = (now - lastFrameStamp) * TicksToMs;
			// Ignore idle gaps - CompositionTarget.Rendering stops firing when nothing animates,
			// so anything over a quarter second is "the UI was idle", not a dropped frame.
			if (gap < 250.0)
			{
				lock (sync) { frameGaps.Add(gap); }
			}
		}
		lastFrameStamp = now;

		if ((now - lastDumpStamp) * TicksToMs > 5000.0)
		{
			lastDumpStamp = now;
			Dump("5s");
		}
	}

	public static void Dump(string label)
	{
		List<double> gaps;
		Dictionary<string, long> c;
		Dictionary<string, long> t;
		lock (sync)
		{
			if (frameGaps.Count == 0 && counts.Count == 0) return;
			gaps = new List<double>(frameGaps);
			c = new Dictionary<string, long>(counts);
			t = new Dictionary<string, long>(ticks);
			frameGaps.Clear();
			counts.Clear();
			ticks.Clear();
		}

		var sb = new StringBuilder();
		sb.AppendLine($"[PerfProbe:{label}] ----------------------------------------");

		if (gaps.Count > 0)
		{
			gaps.Sort();
			var median = Pick(gaps, 0.50);
			var p95 = Pick(gaps, 0.95);
			var p99 = Pick(gaps, 0.99);
			var worst = gaps[gaps.Count - 1];
			var over16 = gaps.Count(x => x > 16.7);
			var over33 = gaps.Count(x => x > 33.3);
			sb.AppendLine($"  frames {gaps.Count}  median {median:F1}ms ({1000.0 / Math.Max(median, 0.001):F0} fps)"
				+ $"  p95 {p95:F1}ms  p99 {p99:F1}ms  worst {worst:F1}ms");
			sb.AppendLine($"  dropped: {over16} frames >16.7ms ({100.0 * over16 / gaps.Count:F0}%), "
				+ $"{over33} >33.3ms ({100.0 * over33 / gaps.Count:F0}%)");
		}

		foreach (var kvp in c.OrderByDescending(x => t.TryGetValue(x.Key, out var v) ? v : 0))
		{
			t.TryGetValue(kvp.Key, out var tk);
			if (tk > 0)
			{
				var totalMs = tk * TicksToMs;
				sb.AppendLine($"  {kvp.Key,-40} {kvp.Value,8} calls  {totalMs,9:F1} ms total  "
					+ $"{totalMs / kvp.Value * 1000.0,8:F1} us each");
			}
			else
			{
				sb.AppendLine($"  {kvp.Key,-40} {kvp.Value,8} calls");
			}
		}

		DivinityApp.Log(sb.ToString());
	}

	private static double Pick(List<double> sorted, double q)
	{
		var i = (int)(sorted.Count * q);
		if (i >= sorted.Count) i = sorted.Count - 1;
		return sorted[i];
	}
}
