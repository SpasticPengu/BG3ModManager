using DynamicData;

using System.Runtime.Serialization;

namespace DivinityModManager.Models;

[DataContract]
public class DivinityLoadOrderEntry
{
	[DataMember]
	public string UUID { get; set; }

	[DataMember]
	public string Name { get; set; }
	public bool Missing { get; set; }

	public DivinityLoadOrderEntry Clone()
	{
		return new DivinityLoadOrderEntry() { Name = this.Name, UUID = this.UUID, Missing = this.Missing };
	}
}

[DataContract]
public class DivinityLoadOrder : ReactiveObject
{
	private string _lastName;

	[Reactive] public string Name { get; set; }
	[Reactive] public string FilePath { get; set; }
	[Reactive] public DateTime LastModifiedDate { get; set; }

	[Reactive] public bool IsModSettings { get; set; }

	/// <summary>
	/// This is an order from a non-standard order file (info .json, .txt, .tsv).
	/// </summary>
	[Reactive] public bool IsDecipheredOrder { get; set; }

	private readonly ObservableAsPropertyHelper<string> _lastModified;

	public string LastModified => _lastModified.Value;

	[DataMember]
	public List<DivinityLoadOrderEntry> Order { get; set; } = new List<DivinityLoadOrderEntry>();

	public void Add(DivinityModData mod, bool force = false)
	{
		try
		{
			if (Order != null && mod != null)
			{
				if (force)
				{
					Order.Add(mod.ToOrderEntry());
				}
				else
				{
					if (Order.Count > 0)
					{
						bool alreadyInOrder = false;
						foreach (var x in Order)
						{
							if (x != null && x.UUID == mod.UUID)
							{
								alreadyInOrder = true;
								break;
							}
						}
						if (!alreadyInOrder)
						{
							Order.Add(mod.ToOrderEntry());
						}
					}
					else
					{
						Order.Add(mod.ToOrderEntry());
					}
				}
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error adding mod to order:\n{ex}");
		}
	}

	public void Add(DivinityProfileActiveModData mod, bool force = false, bool isMissing = false)
	{
		try
		{
			if (Order != null && mod != null)
			{
				if (force)
				{
					Order.Add(new DivinityLoadOrderEntry()
					{
						UUID = mod.UUID,
						Name = mod.Name,
						Missing = isMissing
					});
				}
				else
				{
					if (Order.Count > 0)
					{
						bool alreadyInOrder = false;
						foreach (var x in Order)
						{
							if (x != null && x.UUID == mod.UUID)
							{
								alreadyInOrder = true;
								break;
							}
						}
						if (!alreadyInOrder)
						{
							Order.Add(new DivinityLoadOrderEntry()
							{
								UUID = mod.UUID,
								Name = mod.Name,
								Missing = isMissing
							});
						}
					}
					else
					{
						Order.Add(new DivinityLoadOrderEntry()
						{
							UUID = mod.UUID,
							Name = mod.Name,
							Missing = isMissing
						});
					}
				}
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error adding mod to order:\n{ex}");
		}
	}

	public void Add(IDivinityModData mod, bool force = false)
	{
		try
		{
			if (Order != null && mod != null)
			{
				if (force)
				{
					Order.Add(new DivinityLoadOrderEntry
					{
						UUID = mod.UUID,
						Name = mod.Name,
					});
				}
				else
				{
					if (Order.Count > 0)
					{
						bool alreadyInOrder = false;
						foreach (var x in Order)
						{
							if (x != null && x.UUID == mod.UUID)
							{
								alreadyInOrder = true;
								break;
							}
						}
						if (!alreadyInOrder)
						{
							Order.Add(new DivinityLoadOrderEntry
							{
								UUID = mod.UUID,
								Name = mod.Name,
							});
						}
					}
					else
					{
						Order.Add(new DivinityLoadOrderEntry
						{
							UUID = mod.UUID,
							Name = mod.Name,
						});
					}
				}
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error adding mod to order:\n{ex}");
		}
	}

	public void AddRange(IEnumerable<DivinityModData> mods, bool replace = false)
	{
		foreach (var mod in mods)
		{
			Add(mod, replace);
		}
	}

	public void AddRange(IEnumerable<IDivinityModData> mods, bool replace = false)
	{
		foreach (var mod in mods)
		{
			Add(mod, replace);
		}
	}

	public void Remove(DivinityModData mod)
	{
		try
		{
			if (Order != null && Order.Count > 0 && mod != null)
			{
				DivinityLoadOrderEntry entry = null;
				foreach (var x in Order)
				{
					if (x != null && x.UUID == mod.UUID)
					{
						entry = x;
						break;
					}
				}
				if (entry != null) Order.Remove(entry);
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error removing mod from order:\n{ex}");
		}
	}

	public void Update(DivinityModData mod)
	{
		try
		{
			if (Order != null && Order.Count > 0)
			{
				var existing = Order.FirstOrDefault(x => x.UUID == mod.UUID);
				if (existing != null)
				{
					existing.UUID = mod.UUID;
					existing.Name = mod.Name;
					existing.Missing = false;
				}
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error removing mod from order:\n{ex}");
		}
	}

	public void RemoveRange(IEnumerable<DivinityModData> mods)
	{
		if (Order.Count > 0 && mods != null)
		{
			foreach (var mod in mods)
			{
				Remove(mod);
			}
		}
	}

	public void Sort(Comparison<DivinityLoadOrderEntry> comparison)
	{
		try
		{
			if (Order.Count > 1)
			{
				Order.Sort(comparison);
			}
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error sorting order:\n{ex}");
		}
	}

	public void SetOrder(IEnumerable<DivinityLoadOrderEntry> nextOrder)
	{
		Order.Clear();
		Order.AddRange(nextOrder);
	}

	public void SetOrder(DivinityLoadOrder nextOrder)
	{
		Order.Clear();
		Order.AddRange(nextOrder.Order);
	}

	/// <summary>
	/// Rebuilds the order from the active mods. Entries marked Missing (mods that weren't installed when the order was loaded)
	/// have no active mod, so they are put back after the entry they followed instead of being dropped.
	/// </summary>
	public void UpdateFrom(IEnumerable<DivinityModData> activeMods)
	{
		var previousOrder = Order.ToList();
		Order.Clear();
		AddRange(activeMods, true);

		if (previousOrder.Any(x => x.Missing))
		{
			//File each missing entry under the nearest earlier entry that is still in the order (null = the start)
			var present = Order.Select(x => x.UUID).ToHashSet();
			string anchor = null;
			var missing = new List<(string Anchor, DivinityLoadOrderEntry Entry)>();
			foreach (var entry in previousOrder)
			{
				if (present.Contains(entry.UUID))
				{
					anchor = entry.UUID;
				}
				else if (entry.Missing)
				{
					missing.Add((anchor, entry));
				}
			}
			var missingAfter = missing.ToLookup(x => x.Anchor, x => x.Entry);
			var rebuilt = Order.ToList();
			Order.Clear();
			Order.AddRange(missingAfter[null]);
			foreach (var entry in rebuilt)
			{
				Order.Add(entry);
				Order.AddRange(missingAfter[entry.UUID]);
			}
		}
	}

	public bool OrderEquals(IEnumerable<string> orderList)
	{
		if (Order.Count > 0)
		{
			return Order.Select(x => x.UUID).SequenceEqual(orderList);
		}
		return false;
	}

	public DivinityLoadOrder Clone()
	{
		return new DivinityLoadOrder()
		{
			Name = this.Name,
			Order = this.Order.ToList(),
			LastModifiedDate = this.LastModifiedDate
		};
	}

	public DivinityLoadOrder()
	{
		this.WhenAnyValue(x => x.Name, (name) => !String.IsNullOrEmpty(name) && name != _lastName).Subscribe(_ =>
		{
			DivinityApp.Events.OnOrderNameChanged(_lastName, Name);
			_lastName = Name;
		});
		_lastModified = this.WhenAnyValue(x => x.LastModifiedDate).Select(x => x.ToString("g")).ToProperty(this, nameof(LastModified));
	}
}
