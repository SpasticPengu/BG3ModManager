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
	private static int lastGen0;
	private static int lastGen1;
	private static int lastGen2;

	private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

	public static bool Enabled => enabled;

	/// <summary>
	/// Experiment flags, read from the contents of the "perfprobe" file (comma or whitespace
	/// separated). Lets one build run several A/B variants without editing XAML between runs.
	/// </summary>
	public static string Variant { get; private set; } = "";
	private static HashSet<string> variantFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public static bool Has(string flag) => enabled && variantFlags.Contains(flag);

	/// <summary>Set false while a scripted workload runs, so it lands in one summary instead of 5s slices.</summary>
	public static bool PeriodicDumps { get; set; } = true;

	private static readonly Stack<long> opStarts = new Stack<long>();

	public static void Start(string variant = "")
	{
		if (enabled) return;
		enabled = true;
		variantFlags = new HashSet<string>((variant ?? "").Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
		Variant = variantFlags.Count > 0 ? String.Join(",", variantFlags.OrderBy(x => x)) : "baseline";
		lastFrameStamp = 0;
		lastDumpStamp = Stopwatch.GetTimestamp();
		CompositionTarget.Rendering += OnRendering;
		// Layout and rendering for a frame both run inside one Render-priority dispatcher operation,
		// so timing those operations captures the whole per-frame cost no matter which element the
		// layout manager starts from - unlike the per-override probes, which only see what is routed
		// through the overridden element.
		var hooks = System.Windows.Threading.Dispatcher.CurrentDispatcher.Hooks;
		hooks.OperationStarted += OnOperationStarted;
		hooks.OperationCompleted += OnOperationCompleted;
		DivinityApp.Log($"[PerfProbe] active (variant: {Variant}) - scroll the list, then close the app for a summary.");
	}

	public static void Stop()
	{
		if (!enabled) return;
		CompositionTarget.Rendering -= OnRendering;
		var hooks = System.Windows.Threading.Dispatcher.CurrentDispatcher.Hooks;
		hooks.OperationStarted -= OnOperationStarted;
		hooks.OperationCompleted -= OnOperationCompleted;
		enabled = false;
		Dump("final");
	}

	private static void OnOperationStarted(object sender, System.Windows.Threading.DispatcherHookEventArgs e)
	{
		opStarts.Push(Stopwatch.GetTimestamp());
	}

	private static void OnOperationCompleted(object sender, System.Windows.Threading.DispatcherHookEventArgs e)
	{
		if (opStarts.Count == 0) return;
		var start = opStarts.Pop();
		// Only outermost operations - a nested one is already inside its parent's time.
		if (opStarts.Count > 0) return;
		Record(e.Operation.Priority == System.Windows.Threading.DispatcherPriority.Render
			? "UI thread: Render ops (layout+render)"
			: "UI thread: other dispatcher ops", Stopwatch.GetTimestamp() - start);
	}

	private static readonly Dictionary<string, int> stackSamples = new Dictionary<string, int>();

	/// <summary>
	/// Logs the call stack on the Nth, 2Nth... hit of <paramref name="label"/>, at most
	/// <paramref name="max"/> times. Shows which code path is entering a timed method.
	/// </summary>
	public static void SampleStack(string label, int every, int max = 2)
	{
		if (!enabled) return;
		stackSamples.TryGetValue(label, out var n);
		stackSamples[label] = ++n;
		if (n % every != 0 || n / every > max) return;
		var frames = new StackTrace(1, false).GetFrames()
			.Select(f => f.GetMethod())
			.Where(m => m != null)
			.Take(40)
			.Select(m => $"      {m.DeclaringType?.Name}.{m.Name}");
		DivinityApp.Log($"[PerfProbe:stack] {label} (hit {n})\n{String.Join("\n", frames)}");
	}

	/// <summary>Returns a timestamp to pass to <see cref="End"/>, or 0 when disabled.</summary>
	public static long Begin()
	{
		return enabled ? Stopwatch.GetTimestamp() : 0L;
	}

	public static void End(string name, long start)
	{
		if (!enabled || start == 0L) return;
		Record(name, Stopwatch.GetTimestamp() - start);
	}

	public static void Record(string name, long elapsed)
	{
		if (!enabled) return;
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

	/// <summary>A one-off diagnostic line, emitted immediately rather than aggregated.</summary>
	public static void Note(string message)
	{
		if (!enabled) return;
		DivinityApp.Log($"[PerfProbe:note] {message}");
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

		if (PeriodicDumps && (now - lastDumpStamp) * TicksToMs > 5000.0)
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
		sb.AppendLine($"[PerfProbe:{label}] variant={Variant} ----------------------------------------");

		// Spiky frame times usually mean GC, so always report collections since the last dump.
		var g0 = GC.CollectionCount(0);
		var g1 = GC.CollectionCount(1);
		var g2 = GC.CollectionCount(2);
		sb.AppendLine($"  GC since last dump: gen0 {g0 - lastGen0}, gen1 {g1 - lastGen1}, gen2 {g2 - lastGen2}"
			+ $"  |  heap {GC.GetTotalMemory(false) / 1048576.0:F0} MB");
		lastGen0 = g0; lastGen1 = g1; lastGen2 = g2;

		// If this is true, WPF does accessibility bookkeeping during layout, and the custom peers
		// on every row walk the whole visual subtree to answer it.
		sb.AppendLine("  UI Automation clients listening: "
			+ System.Windows.Automation.Provider.AutomationInteropProvider.ClientsAreListening);

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
