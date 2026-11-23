namespace DivinityModManager.Models.App;

public class ModSettingsParseResults
{
	public List<DivinityProfileActiveModData> ActiveMods { get; set; }

	/// <summary>Why modsettings.lsx could not be read at all (not valid XML, or not readable), or null.</summary>
	public string ReadError { get; set; }

	public int CountActive(bool includeIgnoredMods = false)
	{
		var i = 0;
		foreach (var mod in ActiveMods)
		{
			if (includeIgnoredMods || !DivinityApp.IgnoredMods.Lookup( mod.UUID).HasValue)
			{
				i++;
			}
		}
		return i;
	}
}
