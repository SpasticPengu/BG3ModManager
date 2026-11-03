using System.Globalization;
using System.Text;

namespace DivinityModManager.Search;

/// <summary>
/// A precomputed, folded match key for one string (a mod name, display name or author).
/// Built once per source string; immutable, so it can be read from any thread.
/// </summary>
public sealed class SearchKey
{
	public static readonly SearchKey Empty = new(String.Empty, String.Empty, Array.Empty<byte>(), Array.Empty<int>());

	/// <summary>The string this key was built from. Compared by reference to detect a stale key.</summary>
	public string Source { get; }
	/// <summary>Folded text: lower-case, diacritics stripped (Latin/Greek/Cyrillic only), width/compatibility folded.</summary>
	public string Text { get; }
	/// <summary>Per folded char: the fzf-style boundary bonus of matching here.</summary>
	public byte[] Bonus { get; }
	/// <summary>Per folded char: index of the source char it came from (for highlighting).</summary>
	public int[] SourceIndex { get; }

	private SearchKey(string source, string text, byte[] bonus, int[] sourceIndex)
	{
		Source = source;
		Text = text;
		Bonus = bonus;
		SourceIndex = sourceIndex;
	}

	public static SearchKey Build(string source)
	{
		if (String.IsNullOrEmpty(source)) return Empty;

		var sb = new StringBuilder(source.Length);
		var bonus = new List<byte>(source.Length);
		var map = new List<int>(source.Length);
		var prevClass = CharClass.White;

		for (var i = 0; i < source.Length; i++)
		{
			var c = source[i];
			var cls = SearchText.Classify(source, i);
			var b = FuzzyMatcher.BonusFor(prevClass, cls);
			var start = sb.Length;
			SearchText.FoldChar(source, i, sb);
			for (var k = start; k < sb.Length; k++)
			{
				//Only the first folded char of an expansion (ß -> ss) carries the boundary bonus
				bonus.Add(k == start ? (byte)b : (byte)0);
				map.Add(i);
			}
			if (Char.IsHighSurrogate(c) && i + 1 < source.Length && Char.IsLowSurrogate(source[i + 1]))
			{
				//Keep the pair together: copy the low surrogate unchanged, no bonus
				sb.Append(source[i + 1]);
				bonus.Add(0);
				map.Add(i + 1);
				i++;
			}
			if (sb.Length > start)
			{
				prevClass = cls;
			}
		}

		return new SearchKey(source, sb.ToString(), bonus.ToArray(), map.ToArray());
	}
}

public enum CharClass : byte
{
	White,
	NonWord,
	Delimiter,
	Lower,
	Upper,
	Letter,
	Number
}

public static class SearchText
{
	/// <summary>Fold a query token the same way keys are folded, so both sides compare ordinally.</summary>
	public static string Fold(string s)
	{
		if (String.IsNullOrEmpty(s)) return String.Empty;
		var sb = new StringBuilder(s.Length);
		for (var i = 0; i < s.Length; i++)
		{
			FoldChar(s, i, sb);
		}
		return sb.ToString();
	}

	public static CharClass Classify(string s, int i)
	{
		var c = s[i];
		if (c < 0x80)
		{
			if (c >= 'a' && c <= 'z') return CharClass.Lower;
			if (c >= 'A' && c <= 'Z') return CharClass.Upper;
			if (c >= '0' && c <= '9') return CharClass.Number;
			if (c == ' ' || c == '\t') return CharClass.White;
			if (c == '/' || c == ',' || c == ':' || c == ';' || c == '|') return CharClass.Delimiter;
			return CharClass.NonWord;
		}
		switch (CharUnicodeInfo.GetUnicodeCategory(s, i))
		{
			case UnicodeCategory.LowercaseLetter:
				return CharClass.Lower;
			case UnicodeCategory.UppercaseLetter:
			case UnicodeCategory.TitlecaseLetter:
				return CharClass.Upper;
			case UnicodeCategory.OtherLetter:
			case UnicodeCategory.ModifierLetter:
			case UnicodeCategory.NonSpacingMark:
			case UnicodeCategory.SpacingCombiningMark:
				return CharClass.Letter;
			case UnicodeCategory.DecimalDigitNumber:
			case UnicodeCategory.LetterNumber:
			case UnicodeCategory.OtherNumber:
				return CharClass.Number;
			case UnicodeCategory.SpaceSeparator:
				return CharClass.White;
			default:
				return CharClass.NonWord;
		}
	}

	// Ranges where NFKD + dropping combining marks is a fold users expect (é -> e, ﬁ -> fi, Ａ -> a).
	// Kana and Hangul are deliberately excluded: NFKD would turn ガ into カ + U+3099 and 한 into jamo.
	private static bool IsFoldable(char c)
	{
		return c < 0x0250
			|| (c >= 0x0370 && c < 0x0530)
			|| (c >= 0x1E00 && c < 0x2000)
			|| (c >= 0x2100 && c < 0x2190)
			|| (c >= 0x2460 && c < 0x2500)
			|| (c >= 0xFB00 && c < 0xFB07)
			|| (c >= 0xFF00 && c < 0xFF5F);
	}

	public static void FoldChar(string s, int i, StringBuilder sb)
	{
		var c = s[i];
		if (c < 0x80)
		{
			sb.Append(c >= 'A' && c <= 'Z' ? (char)(c | 0x20) : c);
			return;
		}
		if (Char.IsSurrogate(c))
		{
			//Astral chars (CJK Ext B, emoji) are copied as is; the caller copies the low half
			if (Char.IsHighSurrogate(c)) sb.Append(c);
			return;
		}
		switch (c)
		{
			case 'ß': sb.Append("ss"); return;
			case 'ẞ': sb.Append("ss"); return;
			case 'æ': case 'Æ': sb.Append("ae"); return;
			case 'œ': case 'Œ': sb.Append("oe"); return;
			case 'ø': case 'Ø': sb.Append('o'); return;
			case 'đ': case 'Đ': sb.Append('d'); return;
			case 'ł': case 'Ł': sb.Append('l'); return;
			case 'ı': sb.Append('i'); return;
			case 'þ': case 'Þ': sb.Append("th"); return;
			case 'ς': sb.Append('σ'); return;
		}
		if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
		{
			//A combining mark already in the source: drop it after a foldable base, keep it otherwise
			if (i > 0 && IsFoldable(s[i - 1])) return;
			sb.Append(c);
			return;
		}
		if (!IsFoldable(c))
		{
			sb.Append(Char.ToLowerInvariant(c));
			return;
		}
		var decomposed = c.ToString().Normalize(NormalizationForm.FormKD);
		foreach (var d in decomposed)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark) continue;
			sb.Append(Char.ToLowerInvariant(d));
		}
	}
}
