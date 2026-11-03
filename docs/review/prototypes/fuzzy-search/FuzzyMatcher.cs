namespace DivinityModManager.Search;

public enum FuzzyStrictness
{
	/// <summary>Any subsequence, ranked (inactive list).</summary>
	Full,
	/// <summary>Substring, or every matched char starts a word or continues a run (active list).</summary>
	WordPrefix
}

/// <summary>
/// fzf-v2-style subsequence scorer (Smith-Waterman with affine gaps, restricted to the window
/// found by a greedy pre-pass). Pure, allocation-free on the scoring path, toolkit-neutral.
/// Constants follow junegunn/fzf src/algo/algo.go.
/// </summary>
public static class FuzzyMatcher
{
	public const int NoMatch = Int32.MinValue;

	public const int ScoreMatch = 16;
	public const int ScoreGapStart = -3;
	public const int ScoreGapExtension = -1;
	public const int BonusBoundary = ScoreMatch / 2;
	public const int BonusNonWord = ScoreMatch / 2;
	public const int BonusCamel123 = BonusBoundary + ScoreGapExtension;
	public const int BonusConsecutive = -(ScoreGapStart + ScoreGapExtension);
	public const int BonusFirstCharMultiplier = 2;
	public const int BonusBoundaryWhite = BonusBoundary + 2;
	public const int BonusBoundaryDelimiter = BonusBoundary + 1;

	/// <summary>Above this many DP cells for one key, fall back to scoring the greedy alignment.</summary>
	public const int MaxCells = 2048;

	/// <summary>A gap that ends on a word start costs at most this much, so acronyms (mcm) survive long names.</summary>
	public const int MaxBoundaryGapPenalty = ScoreGapStart + 2 * ScoreGapExtension;

	/// <summary>
	/// Full mode: a gap may end mid-word only if it skipped at most this many chars (typo-ish: tavrn -> tavern).
	/// Longer gaps must land on a word start. Kills scattered hits like imp -> "Icons coMPatibility".
	/// </summary>
	public const int MaxInWordGap = 2;

	private const int Neg = Int32.MinValue / 4;

	public static int BonusFor(CharClass prev, CharClass cur)
	{
		var curIsWord = cur >= CharClass.Lower;
		if (curIsWord)
		{
			switch (prev)
			{
				case CharClass.White: return BonusBoundaryWhite;
				case CharClass.Delimiter: return BonusBoundaryDelimiter;
				case CharClass.NonWord: return BonusBoundary;
			}
		}
		if (prev == CharClass.Lower && cur == CharClass.Upper) return BonusCamel123;
		if (prev != CharClass.Number && cur == CharClass.Number) return BonusCamel123;
		//Script change between cased letters and uncased letters (Latin <-> CJK) counts as a word start
		if ((prev == CharClass.Lower || prev == CharClass.Upper) && cur == CharClass.Letter) return BonusCamel123;
		if (prev == CharClass.Letter && (cur == CharClass.Lower || cur == CharClass.Upper)) return BonusCamel123;
		switch (cur)
		{
			case CharClass.NonWord:
			case CharClass.Delimiter:
				return BonusNonWord;
			case CharClass.White:
				return BonusBoundaryWhite;
		}
		return 0;
	}

	/// <summary>
	/// Score a folded pattern (no whitespace) against a key. Returns NoMatch if the pattern is not a
	/// subsequence (or, for WordPrefix, has no acceptable alignment).
	/// </summary>
	public static int Score(string pattern, SearchKey key, FuzzyStrictness strictness)
	{
		var text = key.Text;
		var m = pattern.Length;
		var n = text.Length;
		if (m == 0) return 0;
		if (m > n) return NoMatch;

		//Phase 1: greedy forward pass. Rejects most non-matches with m vectorised IndexOf calls.
		var first = text.IndexOf(pattern[0]);
		if (first < 0) return NoMatch;
		var idx = first;
		for (var i = 1; i < m; i++)
		{
			idx = text.IndexOf(pattern[i], idx + 1);
			if (idx < 0) return NoMatch;
		}
		var last = text.LastIndexOf(pattern[m - 1]);
		var w = last - first + 1;

		if ((long)m * w > MaxCells)
		{
			return strictness == FuzzyStrictness.Full ? ScoreGreedy(pattern, key, first) : ScoreWordPrefixGreedy(pattern, key);
		}

		//Phase 2: DP over the window [first, last].
		Span<int> buffer = w <= 256 ? stackalloc int[w * 8] : new int[w * 8];
		var ePrev = buffer.Slice(0, w);
		var gPrev = buffer.Slice(w, w);
		var bPrev = buffer.Slice(2 * w, w);
		var eCur = buffer.Slice(3 * w, w);
		var gCur = buffer.Slice(4 * w, w);
		var bCur = buffer.Slice(5 * w, w);
		var hPrev = buffer.Slice(6 * w, w);
		var hCur = buffer.Slice(7 * w, w);
		var bonus = key.Bonus;
		var strict = strictness == FuzzyStrictness.WordPrefix;

		//Row 0
		var p0 = pattern[0];
		var g = Neg;
		var h = Neg;
		var prevE = Neg;
		for (var j = 0; j < w; j++)
		{
			var t = first + j;
			var e = Neg;
			if (text[t] == p0 && (bonus[t] >= BonusCamel123))
			{
				e = ScoreMatch + bonus[t] * BonusFirstCharMultiplier;
			}
			ePrev[j] = e;
			bPrev[j] = bonus[t];
			//gPrev[j] = best score with p0 matched at some k < j - 1 and chars k+1..j-1 skipped
			gPrev[j] = g;
			//hPrev[j] = best score with p0 matched at any k < j - 1 (undecayed, for boundary gaps)
			hPrev[j] = h;
			g = Math.Max(g + ScoreGapExtension, prevE + ScoreGapStart);
			h = Math.Max(h, prevE);
			prevE = e;
		}

		for (var i = 1; i < m; i++)
		{
			var pc = pattern[i];
			g = Neg;
			h = Neg;
			prevE = Neg;
			for (var j = 0; j < w; j++)
			{
				var t = first + j;
				var e = Neg;
				var chunk = 0;
				if (j >= i && text[t] == pc)
				{
					var b = bonus[t];
					var cons = Neg;
					var chunkStart = 0;
					if (ePrev[j - 1] > Neg)
					{
						chunkStart = bPrev[j - 1];
						var cb = b >= BonusBoundary && b > chunkStart ? b : Math.Max(b, Math.Max(BonusConsecutive, chunkStart));
						cons = ePrev[j - 1] + ScoreMatch + cb;
						if (b >= BonusBoundary && b > chunkStart) chunkStart = b;
					}
					var gap = Neg;
					if (b >= BonusCamel123)
					{
						//Word start: any gap length, decayed, and capped when it is a real boundary
						if (gPrev[j] > Neg) gap = gPrev[j] + ScoreMatch + b;
						if (b >= BonusBoundary && hPrev[j] > Neg)
						{
							gap = Math.Max(gap, hPrev[j] + MaxBoundaryGapPenalty + ScoreMatch + b);
						}
					}
					else if (!strict)
					{
						//Mid-word: only a short gap (1..MaxInWordGap skipped chars)
						for (var L = 1; L <= MaxInWordGap && j - 1 - L >= 0; L++)
						{
							var k = j - 1 - L;
							if (ePrev[k] > Neg)
							{
								gap = Math.Max(gap, ePrev[k] + ScoreMatch + b + ScoreGapStart + (L - 1) * ScoreGapExtension);
							}
						}
					}
					if (cons >= gap)
					{
						e = cons;
						chunk = chunkStart;
					}
					else
					{
						e = gap;
						chunk = b;
					}
				}
				eCur[j] = e;
				bCur[j] = chunk;
				gCur[j] = g;
				hCur[j] = h;
				g = Math.Max(g + ScoreGapExtension, prevE + ScoreGapStart);
				h = Math.Max(h, prevE);
				prevE = e;
			}
			var swapH = hPrev; hPrev = hCur; hCur = swapH;
			var swapE = ePrev; ePrev = eCur; eCur = swapE;
			var swapG = gPrev; gPrev = gCur; gCur = swapG;
			var swapB = bPrev; bPrev = bCur; bCur = swapB;
		}

		var best = Neg;
		for (var j = m - 1; j < w; j++)
		{
			if (ePrev[j] > best) best = ePrev[j];
		}
		return best > Neg ? best : NoMatch;
	}

	/// <summary>fzf v1 fallback for very long keys: score the leftmost greedy alignment.</summary>
	private static int ScoreGreedy(string pattern, SearchKey key, int first)
	{
		var text = key.Text;
		var bonus = key.Bonus;
		var score = 0;
		var prevPos = -2;
		var idx = first;
		for (var i = 0; i < pattern.Length; i++)
		{
			idx = i == 0 ? first : text.IndexOf(pattern[i], prevPos + 1);
			int b = bonus[idx];
			if (i == 0) score += ScoreMatch + b * BonusFirstCharMultiplier;
			else if (idx == prevPos + 1) score += ScoreMatch + Math.Max(b, BonusConsecutive);
			else score += ScoreMatch + b + ScoreGapStart + (idx - prevPos - 2) * ScoreGapExtension;
			prevPos = idx;
		}
		return score;
	}

	private static int ScoreWordPrefixGreedy(string pattern, SearchKey key)
	{
		//Long keys in strict mode: accept substring hits only.
		var pos = key.Text.IndexOf(pattern, StringComparison.Ordinal);
		return pos < 0 ? NoMatch : pattern.Length * (ScoreMatch + BonusConsecutive) + key.Bonus[pos] * BonusFirstCharMultiplier;
	}

	/// <summary>
	/// Matched positions in the folded key for highlighting; run only for realized rows.
	/// Re-runs the DP with full matrices and backtracks. Returns false if no match.
	/// </summary>
	public static bool TryGetPositions(string pattern, SearchKey key, FuzzyStrictness strictness, List<int> positions)
	{
		positions.Clear();
		var text = key.Text;
		var m = pattern.Length;
		var n = text.Length;
		if (m == 0 || m > n) return false;
		var strict = strictness == FuzzyStrictness.WordPrefix;
		var bonus = key.Bonus;
		var E = new int[m, n];
		var G = new int[m, n];
		var B = new int[m, n];
		var fromCons = new bool[m, n];
		var back = new int[m, n];
		var gFrom = new int[m, n];
		var H = new int[m, n];
		var hFrom = new int[m, n];
		for (var i = 0; i < m; i++)
		{
			var g = Neg;
			var gArg = -1;
			var h = Neg;
			var hArg = -1;
			var prevE = Neg;
			for (var j = 0; j < n; j++)
			{
				var e = Neg;
				var chunk = 0;
				var cons = false;
				if (j >= i && text[j] == pattern[i])
				{
					var b = bonus[j];
					if (i == 0)
					{
						if (b >= BonusCamel123) e = ScoreMatch + b * BonusFirstCharMultiplier;
						chunk = b;
					}
					else
					{
						var c = Neg;
						var chunkStart = 0;
						if (j > 0 && E[i - 1, j - 1] > Neg)
						{
							chunkStart = B[i - 1, j - 1];
							var cb = b >= BonusBoundary && b > chunkStart ? b : Math.Max(b, Math.Max(BonusConsecutive, chunkStart));
							c = E[i - 1, j - 1] + ScoreMatch + cb;
							if (b >= BonusBoundary && b > chunkStart) chunkStart = b;
						}
						var gp = Neg;
						var gpFrom = -1;
						if (b >= BonusCamel123)
						{
							if (G[i - 1, j] > Neg) { gp = G[i - 1, j] + ScoreMatch + b; gpFrom = gFrom[i - 1, j]; }
							if (b >= BonusBoundary && H[i - 1, j] > Neg && H[i - 1, j] + MaxBoundaryGapPenalty + ScoreMatch + b > gp)
							{
								gp = H[i - 1, j] + MaxBoundaryGapPenalty + ScoreMatch + b;
								gpFrom = hFrom[i - 1, j];
							}
						}
						else if (!strict)
						{
							for (var L = 1; L <= MaxInWordGap && j - 1 - L >= 0; L++)
							{
								var k = j - 1 - L;
								if (E[i - 1, k] > Neg && E[i - 1, k] + ScoreMatch + b + ScoreGapStart + (L - 1) * ScoreGapExtension > gp)
								{
									gp = E[i - 1, k] + ScoreMatch + b + ScoreGapStart + (L - 1) * ScoreGapExtension;
									gpFrom = k;
								}
							}
						}
						if (c >= gp) { e = c; chunk = chunkStart; cons = c > Neg; }
						else { e = gp; chunk = b; back[i, j] = gpFrom; }
					}
				}
				E[i, j] = e;
				B[i, j] = chunk;
				fromCons[i, j] = cons;
				G[i, j] = g;
				gFrom[i, j] = gArg;
				H[i, j] = h;
				hFrom[i, j] = hArg;
				if (prevE > h)
				{
					h = prevE;
					hArg = j - 1;
				}
				if (prevE > Neg && prevE + ScoreGapStart >= g + ScoreGapExtension)
				{
					g = prevE + ScoreGapStart;
					gArg = j - 1;
				}
				else
				{
					g += ScoreGapExtension;
				}
				prevE = e;
			}
		}
		var bestJ = -1;
		var best = Neg;
		for (var j = 0; j < n; j++)
		{
			if (E[m - 1, j] > best) { best = E[m - 1, j]; bestJ = j; }
		}
		if (bestJ < 0) return false;
		var jj = bestJ;
		for (var i = m - 1; i >= 0; i--)
		{
			positions.Add(jj);
			if (i == 0) break;
			jj = fromCons[i, jj] ? jj - 1 : back[i, jj];
		}
		positions.Reverse();
		return true;
	}
}
