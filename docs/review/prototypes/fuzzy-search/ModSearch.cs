namespace DivinityModManager.Search;

/// <summary>Stand-in for DivinityModData in the prototype: just the fields search reads.</summary>
public sealed class FakeMod
{
	public string Name { get; set; }
	public string DisplayName { get; set; }
	public string Author { get; set; }

	private SearchKey _nameKey = SearchKey.Empty;
	private SearchKey _displayKey = SearchKey.Empty;

	//Lazy, rebuilt only when the source string reference changes. Benign race if read off-thread:
	//both writers compute the same immutable key.
	public SearchKey NameKey
	{
		get
		{
			var source = Name ?? String.Empty;
			var key = _nameKey;
			if (!ReferenceEquals(key.Source, source)) _nameKey = key = SearchKey.Build(source);
			return key;
		}
	}

	public SearchKey DisplayNameKey
	{
		get
		{
			var source = DisplayName ?? String.Empty;
			if (ReferenceEquals(source, Name)) return NameKey;
			var key = _displayKey;
			if (!ReferenceEquals(key.Source, source)) _displayKey = key = SearchKey.Build(source);
			return key;
		}
	}
}

public readonly record struct ModMatch(int Tier, int Score, int KeyLength);

public sealed class FreeTextQuery
{
	/// <summary>Average per-char score a pure-subsequence (tier 3) hit must reach to count.</summary>
	public const int MinAverageFuzzyScore = 0;

	private readonly string[] _tokens;
	private readonly bool[] _exact;

	public bool IsEmpty => _tokens.Length == 0;

	public FreeTextQuery(string text)
	{
		var tokens = new List<string>();
		var exact = new List<bool>();
		if (!String.IsNullOrWhiteSpace(text))
		{
			var i = 0;
			while (i < text.Length)
			{
				if (Char.IsWhiteSpace(text[i])) { i++; continue; }
				if (text[i] == '"')
				{
					var end = text.IndexOf('"', i + 1);
					if (end < 0) end = text.Length;
					var phrase = SearchText.Fold(text.Substring(i + 1, end - i - 1).Trim());
					if (phrase.Length > 0) { tokens.Add(phrase); exact.Add(true); }
					i = end + 1;
					continue;
				}
				var start = i;
				while (i < text.Length && !Char.IsWhiteSpace(text[i]) && text[i] != '"') i++;
				var tok = SearchText.Fold(text.Substring(start, i - start));
				if (tok.Length > 0) { tokens.Add(tok); exact.Add(false); }
			}
		}
		_tokens = tokens.ToArray();
		_exact = exact.ToArray();
	}

	public bool TryMatch(FakeMod mod, FuzzyStrictness strictness, out ModMatch match)
	{
		match = default;
		if (_tokens.Length == 0) return true;
		var nameKey = mod.NameKey;
		var displayKey = mod.DisplayNameKey;
		var worstTier = 0;
		var total = 0;
		for (var t = 0; t < _tokens.Length; t++)
		{
			if (!TryMatchToken(_tokens[t], _exact[t], nameKey, strictness, out var tier, out var score))
			{
				tier = Int32.MaxValue;
				score = FuzzyMatcher.NoMatch;
			}
			if (!ReferenceEquals(displayKey, nameKey) && TryMatchToken(_tokens[t], _exact[t], displayKey, strictness, out var dTier, out var dScore))
			{
				if (dTier < tier || (dTier == tier && dScore > score)) { tier = dTier; score = dScore; }
			}
			if (tier == Int32.MaxValue) return false;
			if (tier > worstTier) worstTier = tier;
			total += score;
		}
		match = new ModMatch(worstTier, total, displayKey.Text.Length);
		return true;
	}

	private static bool TryMatchToken(string token, bool exact, SearchKey key, FuzzyStrictness strictness, out int tier, out int score)
	{
		var text = key.Text;
		var pos = text.IndexOf(token, StringComparison.Ordinal);
		if (exact)
		{
			tier = pos == 0 ? 0 : pos > 0 && key.Bonus[pos] >= FuzzyMatcher.BonusCamel123 ? 1 : 2;
			score = pos >= 0 ? token.Length * (FuzzyMatcher.ScoreMatch + FuzzyMatcher.BonusConsecutive) : FuzzyMatcher.NoMatch;
			return pos >= 0;
		}
		if (pos >= 0)
		{
			tier = pos == 0 ? 0 : key.Bonus[pos] >= FuzzyMatcher.BonusCamel123 ? 1 : 2;
			score = FuzzyMatcher.Score(token, key, FuzzyStrictness.Full);
			return true;
		}
		tier = 3;
		score = FuzzyMatcher.Score(token, key, strictness);
		if (score == FuzzyMatcher.NoMatch) return false;
		if (strictness == FuzzyStrictness.Full && score < token.Length * MinAverageFuzzyScore) return false;
		return true;
	}
}

public sealed class ModMatchComparer : IComparer<(FakeMod Mod, ModMatch Match)>
{
	public static readonly ModMatchComparer Instance = new();

	public int Compare((FakeMod Mod, ModMatch Match) x, (FakeMod Mod, ModMatch Match) y)
	{
		var c = x.Match.Tier.CompareTo(y.Match.Tier);
		if (c != 0) return c;
		c = y.Match.Score.CompareTo(x.Match.Score);
		if (c != 0) return c;
		c = x.Match.KeyLength.CompareTo(y.Match.KeyLength);
		if (c != 0) return c;
		return String.Compare(x.Mod.DisplayName, y.Mod.DisplayName, StringComparison.OrdinalIgnoreCase);
	}
}
