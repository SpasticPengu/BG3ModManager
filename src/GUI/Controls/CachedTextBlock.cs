using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DivinityModManager.Controls;

/// <summary>
/// A read-only text element for virtualized list rows that shapes each distinct string once.
/// <para>
/// With container recycling, scrolling hands every row's TextBlock a different string, and
/// TextBlock re-shapes it from scratch (turning characters into positioned glyphs) twice per
/// change: once to measure and again to draw. Shaping costs roughly 3-4us per character, which
/// made the mod name the most expensive element in a mod row. This element keeps the shaped
/// glyph runs in a cache shared by every instance, so a name that has been on screen before is
/// only redrawn, not re-shaped.
/// </para>
/// <para>
/// Deliberately minimal: plain text with the inherited font and foreground - no wrapping,
/// trimming, padding or inlines. Hit testing covers the whole layout box and text wider than the
/// element is clipped, both as with TextBlock, and screen readers see it as text.
/// </para>
/// </summary>
public class CachedTextBlock : FrameworkElement
{
	public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(CachedTextBlock),
		new FrameworkPropertyMetadata(String.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

	public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(CachedTextBlock));
	public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(CachedTextBlock));
	public static readonly DependencyProperty FontStyleProperty = TextElement.FontStyleProperty.AddOwner(typeof(CachedTextBlock));
	public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(CachedTextBlock));
	public static readonly DependencyProperty FontStretchProperty = TextElement.FontStretchProperty.AddOwner(typeof(CachedTextBlock));
	public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(CachedTextBlock));

	public string Text
	{
		get => (string)GetValue(TextProperty);
		set => SetValue(TextProperty, value);
	}

	public FontFamily FontFamily
	{
		get => (FontFamily)GetValue(FontFamilyProperty);
		set => SetValue(FontFamilyProperty, value);
	}

	public double FontSize
	{
		get => (double)GetValue(FontSizeProperty);
		set => SetValue(FontSizeProperty, value);
	}

	public FontStyle FontStyle
	{
		get => (FontStyle)GetValue(FontStyleProperty);
		set => SetValue(FontStyleProperty, value);
	}

	public FontWeight FontWeight
	{
		get => (FontWeight)GetValue(FontWeightProperty);
		set => SetValue(FontWeightProperty, value);
	}

	public FontStretch FontStretch
	{
		get => (FontStretch)GetValue(FontStretchProperty);
		set => SetValue(FontStretchProperty, value);
	}

	public Brush Foreground
	{
		get => (Brush)GetValue(ForegroundProperty);
		set => SetValue(ForegroundProperty, value);
	}

	// Everything that changes the shaped result. Foreground is not here: glyph runs carry no
	// brush, so a selection or theme colour change just redraws the same runs.
	private readonly record struct ShapeKey(string Text, Typeface Typeface, double FontSize, double PixelsPerDip,
		TextFormattingMode Mode, FlowDirection FlowDirection, CultureInfo Culture);

	/// <summary>
	/// One replayable step of the shaped text: draw a glyph run, push a guideline set, or pop it.
	/// FormattedText wraps each line in a baseline guideline set for pixel snapping; replaying it
	/// keeps the output identical to drawing the FormattedText directly.
	/// </summary>
	private readonly record struct DrawStep(GlyphRun Run, GuidelineSet Guidelines);

	/// <summary>Steps is null when the text could not be flattened into replayable steps.</summary>
	private sealed record ShapedText(DrawStep[] Steps, double Width, double Height);

	// Comfortably above the ~1100 mods of a heavy load order; at the cap the cache simply starts over.
	private const int MaxCachedStrings = 8192;
	private static readonly Dictionary<ShapeKey, ShapedText> _cache = [];
	private static readonly object _cacheLock = new();

	private ShapeKey _key;
	private ShapedText _shaped;

	protected override Size MeasureOverride(Size availableSize)
	{
		_key = CreateKey();
		_shaped = GetShaped(_key);
		return new Size(_shaped.Width, _shaped.Height);
	}

	protected override void OnRender(DrawingContext drawingContext)
	{
		var foreground = Foreground;
		if (foreground == null) return;

		var key = CreateKey();
		if (_shaped == null || key != _key)
		{
			_key = key;
			_shaped = GetShaped(key);
		}

		if (_shaped.Steps != null)
		{
			foreach (var step in _shaped.Steps)
			{
				if (step.Run != null)
				{
					drawingContext.DrawGlyphRun(foreground, step.Run);
				}
				else if (step.Guidelines != null)
				{
					drawingContext.PushGuidelineSet(step.Guidelines);
				}
				else
				{
					drawingContext.Pop();
				}
			}
		}
		else
		{
			drawingContext.DrawText(CreateFormattedText(key, foreground), new Point());
		}
	}

	protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
	{
		base.OnPropertyChanged(e);
		// Direction and language change the shaping but, unlike the font properties, are not
		// registered as affecting measure.
		if (e.Property == FlowDirectionProperty || e.Property == LanguageProperty)
		{
			InvalidateMeasure();
			InvalidateVisual();
		}
	}

	protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
	{
		base.OnDpiChanged(oldDpi, newDpi);
		InvalidateMeasure();
		InvalidateVisual();
	}

	/// <summary>
	/// Like TextBlock, the whole layout box is hit-testable, not just the glyphs, so a tooltip or
	/// click over the empty part of a stretched cell still lands on this element.
	/// </summary>
	protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
	{
		var bounds = new Rect(new Point(), RenderSize);
		return bounds.Contains(hitTestParameters.HitPoint) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
	}

	protected override AutomationPeer OnCreateAutomationPeer()
	{
		return new CachedTextBlockAutomationPeer(this);
	}

	private ShapeKey CreateKey()
	{
		return new ShapeKey(Text ?? String.Empty, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize,
			VisualTreeHelper.GetDpi(this).PixelsPerDip, TextOptions.GetTextFormattingMode(this), FlowDirection, GetCulture());
	}

	private CultureInfo GetCulture()
	{
		try
		{
			return Language?.GetSpecificCulture() ?? CultureInfo.InvariantCulture;
		}
		catch (InvalidOperationException)
		{
			return CultureInfo.InvariantCulture;
		}
	}

	private static ShapedText GetShaped(ShapeKey key)
	{
		lock (_cacheLock)
		{
			if (_cache.TryGetValue(key, out var cached)) return cached;
		}

		var formatted = CreateFormattedText(key, Brushes.Black);
		var drawing = new DrawingGroup();
		using (var dc = drawing.Open())
		{
			dc.DrawText(formatted, new Point());
		}
		var steps = new List<DrawStep>();
		// FormattedText does not expose its glyph runs, so draw it once and record what it drew.
		// Runs carry their own positions; anything else (a transform, say) means the text is drawn
		// the slow way instead of risking a wrong rendering.
		var height = formatted.Height;
		if (key.Text.Length == 0)
		{
			// TextBlock keeps one empty line's height; FormattedText reports zero.
			height = CreateFormattedText(key with { Text = " " }, Brushes.Black).Height;
		}
		var shaped = new ShapedText(TryCollectSteps(drawing, steps) ? [.. steps] : null, formatted.WidthIncludingTrailingWhitespace, height);

		lock (_cacheLock)
		{
			if (_cache.Count >= MaxCachedStrings) _cache.Clear();
			_cache[key] = shaped;
		}
		return shaped;
	}

	private static FormattedText CreateFormattedText(ShapeKey key, Brush foreground)
	{
		return new FormattedText(key.Text, key.Culture, key.FlowDirection, key.Typeface, key.FontSize, foreground, null, key.Mode, key.PixelsPerDip);
	}

	private static bool TryCollectSteps(Drawing drawing, List<DrawStep> steps)
	{
		switch (drawing)
		{
			case GlyphRunDrawing glyphs:
				steps.Add(new DrawStep(glyphs.GlyphRun, null));
				return true;
			case DrawingGroup group when group.Transform == null && group.ClipGeometry == null && group.OpacityMask == null
				&& group.Opacity == 1.0 && group.BitmapEffect == null:
				GuidelineSet guidelines = null;
				if (group.GuidelineSet != null)
				{
					guidelines = ToReplayableGuidelines(group.GuidelineSet);
					if (guidelines == null) return false;
					steps.Add(new DrawStep(null, guidelines));
				}
				foreach (var child in group.Children)
				{
					if (!TryCollectSteps(child, steps)) return false;
				}
				if (guidelines != null)
				{
					steps.Add(new DrawStep(null, null));
				}
				return true;
			default:
				return false;
		}
	}

	// FormattedText snaps each line to the pixel grid with a "dynamic" guideline pair, written as
	// (leading coordinate, offset). Recorded into a DrawingGroup, the pair is stored verbatim, but
	// DrawingContext.PushGuidelineSet reads a dynamic pair back as (leading, driven coordinate) and
	// subtracts - so replaying the stored set as-is moves the text by a sub-pixel. A dynamic set can
	// only be built through an internal constructor; it is looked up once, and used only when text
	// is first shaped. If it is ever missing, text is drawn the uncached way rather than misplaced.
	private static readonly ConstructorInfo _dynamicGuidelineSetCtor = typeof(GuidelineSet).GetConstructor(
		BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(double[]), typeof(double[]), typeof(bool)], null);
	private static readonly PropertyInfo _isDynamicProperty = typeof(GuidelineSet).GetProperty("IsDynamic", BindingFlags.Instance | BindingFlags.NonPublic);

	/// <summary>Returns a frozen guideline set that replays as the recorded one was drawn, or null if that cannot be guaranteed.</summary>
	private static GuidelineSet ToReplayableGuidelines(GuidelineSet recorded)
	{
		if (_isDynamicProperty?.GetValue(recorded) is not bool isDynamic) return null;
		if (!isDynamic)
		{
			// Static guidelines hold absolute coordinates both ways round.
			if (recorded.CanFreeze) recorded.Freeze();
			return recorded.IsFrozen ? recorded : null;
		}

		var y = recorded.GuidelinesY;
		if (recorded.GuidelinesX?.Count > 0 || y == null || y.Count < 1 || y.Count > 2) return null;
		if (y.Count == 1) return recorded.IsFrozen ? recorded : null;
		if (_dynamicGuidelineSetCtor == null) return null;

		var replayable = (GuidelineSet)_dynamicGuidelineSetCtor.Invoke([null, new[] { y[0], y[0] + y[1] }, true]);
		replayable.Freeze();
		return replayable;
	}

	private sealed class CachedTextBlockAutomationPeer : FrameworkElementAutomationPeer
	{
		public CachedTextBlockAutomationPeer(CachedTextBlock owner) : base(owner) { }

		protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

		protected override string GetClassNameCore() => "TextBlock";

		protected override string GetNameCore()
		{
			var name = base.GetNameCore();
			return String.IsNullOrEmpty(name) ? ((CachedTextBlock)Owner).Text ?? String.Empty : name;
		}

		/// <summary>Mirrors TextBlockAutomationPeer: text from a DataTemplate is content, text inside a ControlTemplate is chrome.</summary>
		protected override bool IsControlElementCore()
		{
			var templatedParent = ((CachedTextBlock)Owner).TemplatedParent;
			return templatedParent == null || templatedParent is ContentPresenter;
		}
	}
}
