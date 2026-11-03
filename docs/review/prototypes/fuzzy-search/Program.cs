using System.Diagnostics;
using System.Globalization;

using DivinityModManager.Search;

var real = new[]
{
	"ImprovedUI", "ImprovedUI Assets", "Mod Configuration Menu", "5e Spells", "Tav's Tavern", "Basket Full of Gale",
	"Party Limit Begone", "WASD Character Movement", "Native Camera Tweaks", "Unlock Level Curve", "Customizer",
	"Compatibility Framework", "Community Library", "Tutorial Chest Summoning", "Better Hotbar 2", "Better Inventory UI",
	"Carry Weight Increased", "Faster Movement Speed", "No Camp Clutter", "Highlight Everything", "Auto Send Food To Camp",
	"Disable Telemetry", "Native Mod Loader", "Script Extender Debug Console", "Fix Stragglers", "Gold Weight Removed",
	"Expanded Epithets", "Unique Tav", "Tavern Brawl", "Tavern Brawl Lite", "Shapeshifter Hat", "Ascended Shadowheart",
	"Shadowheart Tweaks", "Astarion Hair Options", "Karlach Fixes", "Wyll's Sideburns", "Laezel's Lovely Lashes",
	"Halsin Hair", "Minthara Recruitable", "Jaheira Kit", "More Hair Colors", "Hairs of Faerûn", "Kaz's Hair Pack",
	"Mystic Eyes", "Head Wear Toggle", "Camp Clothes Overhaul", "Armour of Ælfwine", "Pokémon Shields", "Köln Hairstyles",
	"Dragonborn Heads Expanded", "Tiefling Horns Pack", "Gith Variety", "Drow Faces", "Mindflayer Playable Race",
	"Homebrew Classes - Witch", "Artificer Class", "Blood Hunter Class", "Warlock Pact Boons", "Druid Wild Shape Plus",
	"Monk Unarmored Fix", "Ranger Favored Foe", "Paladin Oaths Expanded", "Cleric Domains", "Sorcerer Metamagic",
	"Bard Colleges", "Rogue Subclasses", "Fighter Battle Master Plus", "Wizard Schools", "Multiclass Fix",
	"Level 20 Multiclass", "Respec Anytime", "Withers Tweaks", "Unlimited Rests", "Short Rest Heal", "Free Camp Supplies",
	"Smart Autosaving", "Quick Save Fix", "Faster Dialogue", "Skip Intro", "Better Loot Tables", "Vendor Overhaul",
	"Trader Gold Increased", "Pick Up Anything", "Telekinesis Range", "Jump Distance", "Fall Damage Reduced",
	"Darkvision Fix", "Light Cantrip Duration", "Spell Slots Restored", "Scroll Crafting", "Alchemy Overhaul",
	"Dye Pack", "Transmog Enhanced", "Equipment Overhaul", "Weapon Variety", "Magic Items Expanded",
	"=====================", "===== UI =====", "===== Spells =====", "===== Classes =====", "===== Hair =====",
	"====== Fixes ======", "---- Visuals ----", "zzz Overrides Last", "[WIP] Underdark Expansion", "[ALPHA] New Origin",
	"更多发型", "日本語化パッチ", "한국어 번역", "Русский перевод", "Polski Przekład", "Traducción Español",
	"Deutsche Übersetzung", "Ελληνικά", "中文本地化 Chinese Localization", "ﾃｽﾄ Halfwidth Test", "ＦＵＬＬＷＩＤＴＨ Mod",
	"Camera Mod (Top Down)", "ReShade Preset", "Lighting Overhaul", "Weather Effects", "Ambient Sounds",
	"Music Replacer", "Voice Pack Tav", "Dialogue Expansion", "Romance Overhaul", "Companion Banter",
	"Origins Tweaks", "Dark Urge Fixes", "Act 3 Performance", "Baldur's Gate Shops", "Lower City Vendors",
	"Moonrise Towers Fix", "Shadow Cursed Lands Lighting", "Grymforge Tweaks", "Underdark Mushrooms", "Goblin Camp Party",
	"Owlbear Cub Pet", "Scratch Dog Tweaks", "Familiar Variety", "Summon Fixes", "Wild Magic Tables"
};
var vocabA = new[] { "Better", "Improved", "More", "Expanded", "Simple", "Ultimate", "Lite", "Extra", "Fixed", "Enhanced", "Unlimited", "Custom", "Hidden", "Old", "New", "Quick" };
var vocabB = new[] { "Spells", "Armor", "Weapons", "Hair", "Faces", "Tattoos", "Camp", "Loot", "Vendors", "Dyes", "Rings", "Amulets", "Boots", "Cloaks", "Gloves", "Helmets", "Potions", "Scrolls", "Feats", "Races", "Classes", "Subclasses", "Origins", "Companions", "Dialogue", "Inventory", "Hotbar", "Camera", "Lighting", "Textures", "Shaders", "Icons", "Tooltips", "Portraits", "Eyes", "Horns", "Tails", "Wings", "Beards", "Makeup" };
var vocabC = new[] { "", "", "", " Pack", " Overhaul", " Fix", " Tweaks", " Plus", " Redux", " Compatibility Patch", " for Tav", " - Patch 7", " v2", " (WIP)", " Addon", " Framework", " Collection", " Resource" };
var rng = new Random(1234);
var names = new List<string>(real);
while (names.Count < 1121)
{
	var n = vocabA[rng.Next(vocabA.Length)] + " " + vocabB[rng.Next(vocabB.Length)] + vocabC[rng.Next(vocabC.Length)];
	if (rng.Next(5) == 0) n = n.Replace(" ", "");
	if (rng.Next(9) == 0) n = vocabB[rng.Next(vocabB.Length)] + "_" + n.Replace(" ", "_") + "_" + rng.Next(1, 9) + "." + rng.Next(0, 9);
	names.Add(n);
}

var args0 = Environment.GetCommandLineArgs();
if (args0.Contains("--fold"))
{
	foreach (var (src, q) in new[] { ("Straße der Helden", "strasse"), ("İstanbul Hats", "istanbul"), ("ﬁnal ﬁx", "final"), ("ガイド", "カイド"), ("한국어 번역", "한국"), ("Ａｒｍｏｒ", "armor"), ("Pokémon", "pokemon"), ("Mod Configuration Menu", "mcm"), ("ImprovedUI", "iui"), ("Unlock Level Curve", "lvl curve"), ("中文本地化 Chinese Localization", "中文chinese") })
	{
		var key = SearchKey.Build(src);
		var positions = new List<int>();
		var tokens = q.Split(' ');
		var shown = new List<string>();
		foreach (var t in tokens)
		{
			var ft = SearchText.Fold(t);
			var ok = FuzzyMatcher.TryGetPositions(ft, key, FuzzyStrictness.Full, positions);
			var score = FuzzyMatcher.Score(ft, key, FuzzyStrictness.Full);
			shown.Add($"{t}=>{(ok ? String.Join(",", positions.Select(p => key.SourceIndex[p])) : "none")} s{score}");
		}
		Console.WriteLine($"[{src}] key=[{key.Text}] bonus=[{String.Join(" ", key.Bonus)}] :: {String.Join(" | ", shown)}");
	}
	return;
}
Console.WriteLine($"Config: {(Debugger.IsAttached ? "debugger" : "")} optimized={!typeof(FuzzyMatcher).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false).OfType<DebuggableAttribute>().Any(a => a.IsJITOptimizerDisabled)}");
Console.WriteLine($"Corpus: {names.Count} names, avg len {names.Average(x => x.Length):F1}, max {names.Max(x => x.Length)}");

//Key build cost
var sw = Stopwatch.StartNew();
var mods = names.Select(x => new FakeMod { Name = x, DisplayName = x, Author = "Author" }).ToList();
foreach (var m in mods) { _ = m.NameKey; }
sw.Stop();
Console.WriteLine($"Key build (cold, includes JIT): {sw.Elapsed.TotalMilliseconds:F2} ms");
var mods2 = names.Select(x => new FakeMod { Name = new string(x.AsSpan()), DisplayName = null, Author = "Author" }).ToList();
foreach (var m in mods2) m.DisplayName = m.Name;
sw.Restart();
foreach (var m in mods2) { _ = m.NameKey; }
sw.Stop();
Console.WriteLine($"Key build (warm): {sw.Elapsed.TotalMilliseconds:F2} ms for {mods2.Count} mods");

var inactive = mods.Where((x, i) => i % 6 == 0).ToList();
var active = mods.Where((x, i) => i % 6 != 0).ToList();
Console.WriteLine($"Split: inactive {inactive.Count}, active {active.Count}");

var queries = new[] { "a", "e", "ui", "spell", "imp ui", "iui", "tav", "zzzzzz", "tavern brawl", "5e spells", "party limit", "cust", "hairfaerun", "pokemon", "faerun", "日本", "ｆｕｌｌ", "compatibility framework patch" };

Console.WriteLine();
Console.WriteLine("query | inactive(186) Full: matches, µs/pass | active(935) Full: matches, µs/pass | all 1121 Full µs/pass | substring baseline 1121 µs/pass");
foreach (var q in queries)
{
	var query = new FreeTextQuery(q);
	var (inM, inUs) = Bench(query, inactive, FuzzyStrictness.Full);
	var (acM, acUs) = Bench(query, active, FuzzyStrictness.Full);
	var (_, allUs) = Bench(query, mods, FuzzyStrictness.Full);
	var subUs = BenchSubstring(q, mods);
	Console.WriteLine($"{q,-32} | {inM,4} {inUs,8:F1} | {acM,4} {acUs,8:F1} | {allUs,8:F1} | {subUs,8:F1}");
}

//Ranking sort cost on the inactive list
{
	var query = new FreeTextQuery("e");
	var matched = new List<(FakeMod, ModMatch)>();
	foreach (var m in inactive) if (query.TryMatch(m, FuzzyStrictness.Full, out var mm)) matched.Add((m, mm));
	sw.Restart();
	for (var it = 0; it < 200; it++)
	{
		var copy = new List<(FakeMod, ModMatch)>(matched);
		copy.Sort(ModMatchComparer.Instance);
	}
	sw.Stop();
	Console.WriteLine($"\nRank sort of {matched.Count} inactive matches: {sw.Elapsed.TotalMilliseconds * 1000 / 200:F1} µs");
}

//Positions for 28 realized rows
{
	var positions = new List<int>();
	var tok = SearchText.Fold("spell");
	sw.Restart();
	for (var it = 0; it < 100; it++)
	{
		for (var r = 0; r < 28; r++) FuzzyMatcher.TryGetPositions(tok, mods[r].NameKey, FuzzyStrictness.Full, positions);
	}
	sw.Stop();
	Console.WriteLine($"Highlight positions for 28 rows: {sw.Elapsed.TotalMilliseconds * 1000 / 100:F1} µs");
}

//Superset check: every mod today's substring search finds, fuzzy also finds
{
	var rnd = new Random(7);
	var misses = 0;
	var checks = 0;
	for (var it = 0; it < 3000; it++)
	{
		var src = names[rnd.Next(names.Count)];
		if (src.Length < 2) continue;
		var len = rnd.Next(1, Math.Min(8, src.Length));
		var start = rnd.Next(0, src.Length - len);
		var q = src.Substring(start, len);
		if (String.IsNullOrWhiteSpace(q) || q.Contains('"')) continue;
		var query = new FreeTextQuery(q);
		foreach (var m in mods)
		{
			if (CultureInfo.CurrentCulture.CompareInfo.IndexOf(m.Name, q, CompareOptions.IgnoreCase) >= 0)
			{
				checks++;
				if (!query.TryMatch(m, FuzzyStrictness.Full, out _))
				{
					misses++;
					if (misses <= 10) Console.WriteLine($"SUPERSET MISS: [{q}] not found in [{m.Name}]");
				}
			}
		}
	}
	Console.WriteLine($"Superset check: {checks} substring hits, {misses} missed by fuzzy");
}

//Qualitative ranking
foreach (var q in new[] { "tav", "imp ui", "iui", "spell", "hair", "faerun", "pokemon", "shadowheart", "brawl tav", "uber", "日本", "full", "ｃａｍｐ", "\"tavern brawl\"", "level curve", "lvl curve", "configmenu", "mcm", "seb", "zzzzzz" })
{
	var query = new FreeTextQuery(q);
	var full = new List<(FakeMod, ModMatch)>();
	var strictCount = 0;
	foreach (var m in mods)
	{
		if (query.TryMatch(m, FuzzyStrictness.Full, out var mm)) full.Add((m, mm));
		if (query.TryMatch(m, FuzzyStrictness.WordPrefix, out _)) strictCount++;
	}
	var substr = mods.Count(m => CultureInfo.CurrentCulture.CompareInfo.IndexOf(m.Name, q, CompareOptions.IgnoreCase) >= 0);
	full.Sort(ModMatchComparer.Instance);
	Console.WriteLine($"\n[{q}] today(substring)={substr} fuzzyFull={full.Count} wordPrefix={strictCount}");
	foreach (var (m, mm) in full.Take(8))
	{
		Console.WriteLine($"   t{mm.Tier} s{mm.Score,4}  {m.DisplayName}");
	}
	if (full.Count > 8)
	{
		var (lm, lmm) = full[^1];
		Console.WriteLine($"   ... last: t{lmm.Tier} s{lmm.Score,4}  {lm.DisplayName}");
	}
}

static (int, double) Bench(FreeTextQuery query, List<FakeMod> list, FuzzyStrictness strictness)
{
	var count = 0;
	for (var w = 0; w < 20; w++) foreach (var m in list) query.TryMatch(m, strictness, out _);
	var iterations = 300;
	var sw = Stopwatch.StartNew();
	for (var it = 0; it < iterations; it++)
	{
		count = 0;
		foreach (var m in list)
		{
			if (query.TryMatch(m, strictness, out _)) count++;
		}
	}
	sw.Stop();
	return (count, sw.Elapsed.TotalMilliseconds * 1000 / iterations);
}

static double BenchSubstring(string q, List<FakeMod> list)
{
	var iterations = 300;
	var count = 0;
	var sw = Stopwatch.StartNew();
	for (var it = 0; it < iterations; it++)
	{
		foreach (var m in list)
		{
			if (CultureInfo.CurrentCulture.CompareInfo.IndexOf(m.Name, q, CompareOptions.IgnoreCase) >= 0) count++;
		}
	}
	sw.Stop();
	return sw.Elapsed.TotalMilliseconds * 1000 / iterations;
}
