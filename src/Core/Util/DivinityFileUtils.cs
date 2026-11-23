using LSLib.LS;

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace DivinityModManager.Util;

public static class DivinityFileUtils
{
	public static readonly EnumerationOptions RecursiveOptions = new()
	{
		RecurseSubdirectories = true,
		IgnoreInaccessible = true,
		MatchCasing = MatchCasing.CaseInsensitive
	};

	public static readonly EnumerationOptions GameDataOptions = new()
	{
		RecurseSubdirectories = true,
		IgnoreInaccessible = true,
		MaxRecursionDepth = 1,
		MatchCasing = MatchCasing.CaseInsensitive
	};

	public static readonly EnumerationOptions FlatSearchOptions = new()
	{
		RecurseSubdirectories = false,
		IgnoreInaccessible = true,
		MatchCasing = MatchCasing.CaseInsensitive
	};

	/// <summary>
	/// Gets the drive type of the given path.
	/// </summary>
	/// <param name="path">The path.</param>
	/// <returns>DriveType of path</returns>
	public static System.IO.DriveType GetPathDriveType(string path)
	{
		//OK, so UNC paths aren't 'drives', but this is still handy
		if (path.StartsWith(@"\\")) return System.IO.DriveType.Network;
		var info = DriveInfo.GetDrives().Where(i => path.StartsWith(i.Name, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
		if (info == null) return System.IO.DriveType.Unknown;
		return info.DriveType;
	}

	/// <summary>
	/// Check if a directory is the base of another
	/// </summary>
	/// <param name="root">Candidate root</param>
	/// <param name="child">Child folder</param>
	public static bool IsSubdirectoryOf(DirectoryInfo root, DirectoryInfo child)
	{
		var directoryPath = EndsWithSeparator(new Uri(child.FullName).AbsolutePath);
		var rootPath = EndsWithSeparator(new Uri(root.FullName).AbsolutePath);
		return directoryPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Check if a directory is the base of another
	/// </summary>
	/// <param name="root">Candidate root</param>
	/// <param name="child">Child folder</param>
	public static bool IsSubdirectoryOf(string root, string child)
	{
		return IsSubdirectoryOf(new DirectoryInfo(root), new DirectoryInfo(child));
	}

	private static string EndsWithSeparator(string absolutePath)
	{
		return absolutePath?.TrimEnd('/', '\\') + "/";
	}

	/// <summary>
	/// Gets a unique file name if the file already exists.
	/// Source: https://stackoverflow.com/a/13050041
	/// </summary>
	public static string GetUniqueFilename(string fullPath)
	{
		if (!Path.IsPathRooted(fullPath))
			fullPath = Path.GetFullPath(fullPath);
		if (File.Exists(fullPath))
		{
			var filename = Path.GetFileName(fullPath);
			var path = fullPath.Substring(0, fullPath.Length - filename.Length);
			var filenameWOExt = Path.GetFileNameWithoutExtension(fullPath);
			var ext = Path.GetExtension(fullPath);
			var n = 1;
			do
			{
				fullPath = Path.Join(path, String.Format("{0} ({1}){2}", filenameWOExt, (n++), ext));
			}
			while (File.Exists(fullPath));
		}
		return fullPath;
	}


	public static List<string> IgnoredPackageFiles = [
		"ReConHistory.txt",
		"dialoglog.txt",
		"errors.txt",
		"log.txt",
		"personallog.txt",
		"story_orphanqueries_found.txt",
		"goals.div",
		"goals.raw",
		"story.div",
		"story_ac.dat",
		"story_definitions.div",
		"story.div.osi",
		".ailog",
		".log",
		".debugInfo",
		".dmp",
	];

	private static bool IgnoreFile(string targetFilePath, string ignoredFileName)
	{
		if (Path.GetFileName(targetFilePath).Equals(ignoredFileName, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		else if (ignoredFileName.Substring(0) == "." && Path.GetExtension(targetFilePath).Equals(ignoredFileName, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return false;
	}
	#region Package Creation Async
	public static async Task<bool> CreatePackageAsync(string rootPath, List<string> inputPaths, string outputPath, CancellationToken token, List<string> ignoredFiles = null)
	{
		try
		{
			ignoredFiles ??= IgnoredPackageFiles;

			if (token.IsCancellationRequested) return false;

			if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
			{
				rootPath += Path.DirectorySeparatorChar;
			}

			var conversionParams = ResourceConversionParameters.FromGameVersion(DivinityApp.GAME);

			var build = new PackageBuildData
			{
				Version = conversionParams.PAKVersion,
				Compression = CompressionMethod.LZ4,
				CompressionLevel = LSCompressionLevel.Default,
				Priority = 0,
			};

			foreach (var f in inputPaths)
			{
				if (token.IsCancellationRequested) break;
				AddFilesToPackage(f, build, rootPath, ignoredFiles, token);
			}

			DivinityApp.Log($"Writing package '{outputPath}'.");
			using var writer = PackageWriterFactory.Create(build, outputPath);
			writer.Write();
			return true;
		}
		catch (Exception ex)
		{
			if (!token.IsCancellationRequested)
			{
				DivinityApp.Log($"Error creating package: {ex}");
			}
			else
			{
				DivinityApp.Log($"Cancelled creating package: {ex}");
			}
			return false;
		}
	}

	private static void AddFilesToPackage(string filePath, PackageBuildData build, string rootPath, List<string> ignoredFiles, CancellationToken token)
	{
		if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
		{
			rootPath += Path.DirectorySeparatorChar;
		}

		if (Directory.Exists(filePath))
		{
			if (!filePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
			{
				filePath += Path.DirectorySeparatorChar;
			}

			var files = EnumerateFiles(filePath, RecursiveOptions, (f) => !ignoredFiles.Any(x => IgnoreFile(f, x)))
				.ToDictionary(k => k.Replace(rootPath, String.Empty), v => v);

			foreach (var file in files)
			{
				if (token.IsCancellationRequested) break;
				var fileInfo = PackageBuildInputFile.CreateFromFilesystem(file.Value, file.Key);
				build.Files.Add(fileInfo);
			}
		}
		else if (File.Exists(filePath))
		{
			var name = Path.GetRelativePath(rootPath, filePath);
			var fileInfo = PackageBuildInputFile.CreateFromFilesystem(filePath, name);
			build.Files.Add(fileInfo);
		}
	}

	private static Task WritePackageAsync(PackageWriter writer, string outputPath, CancellationToken token)
	{
		var task = Task.Run(async () =>
		{
			// execute actual operation in child task
			var childTask = Task.Factory.StartNew(() =>
			{
				try
				{
					writer.Write();
				}
				catch (Exception)
				{
					// ignored because an exception on a cancellation request 
					// cannot be avoided if the stream gets disposed afterwards 
				}
			}, TaskCreationOptions.AttachedToParent);

			var awaiter = childTask.GetAwaiter();
			while (!awaiter.IsCompleted)
			{
				await Task.Delay(0, token);
			}
		}, token);

		return task;
	}
	#endregion

	/// <summary>
	/// The full path that Path.Join(rootDirectory, relativePath) lands on, or null when that is outside rootDirectory, as a
	/// name with "..\" segments taken from a mod can be. LSLib builds extraction paths with Path.Join the same way.
	/// </summary>
	public static string GetContainedPath(string rootDirectory, string relativePath)
	{
		if (String.IsNullOrWhiteSpace(rootDirectory) || String.IsNullOrWhiteSpace(relativePath)) return null;
		try
		{
			var root = Path.GetFullPath(rootDirectory);
			if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
			var fullPath = Path.GetFullPath(Path.Join(root, relativePath));
			// Ordinal: a path that stays inside keeps root's exact text, while "..\mods" from "Mods" may be another folder
			// where folder names are case-sensitive
			if (fullPath.Length > root.Length && fullPath.StartsWith(root, StringComparison.Ordinal)) return fullPath;
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error resolving '{relativePath}' inside '{rootDirectory}':\n{ex}");
		}
		return null;
	}

	/// <summary>
	/// Extracts a pak, skipping any file whose name would land outside outputDirectory (a crafted pak can name its files
	/// "..\..\something"). Returns false when files were skipped.
	/// </summary>
	private static bool UncompressPackageContained(string pakPath, string outputDirectory, Func<PackagedFileInfo, bool> filter = null)
	{
		var skipped = new List<string>();
		var packager = new Packager();
		packager.UncompressPackage(pakPath, outputDirectory, f =>
		{
			if (filter != null && !filter(f)) return false;
			if (f.IsDeletion() || GetContainedPath(outputDirectory, f.Name) != null) return true;
			skipped.Add(f.Name);
			return false;
		});
		if (skipped.Count == 0) return true;
		DivinityApp.Log($"Skipped {skipped.Count} file(s) in '{pakPath}' that would be written outside '{outputDirectory}':\n\t{String.Join("\n\t", skipped)}");
		return false;
	}

	public static bool ExtractPackages(IEnumerable<string> pakPaths, string outputDirectory)
	{
		var success = 0;
		var count = pakPaths.Count();
		foreach (var path in pakPaths)
		{
			try
			{
				//Put each pak into its own folder
				var destination = Path.Join(outputDirectory, Path.GetFileNameWithoutExtension(path));

				//Unless the foldername == the pak name and we're only extracting one pak
				if (count == 1 && Path.GetDirectoryName(outputDirectory).Equals(Path.GetFileNameWithoutExtension(path)))
				{
					destination = outputDirectory;
				}
				if (UncompressPackageContained(path, destination)) success++;
			}
			catch (Exception ex)
			{
				DivinityApp.Log($"Error extracting package: {ex}");
			}
		}
		return success >= count;
	}

	public static bool ExtractPackage(string pakPath, string outputDirectory)
	{
		try
		{
			return UncompressPackageContained(pakPath, outputDirectory);
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error extracting package: {ex}");
			return false;
		}
	}

	public static async Task<bool> ExtractPackageAsync(string pakPath, string outputDirectory, CancellationToken token, Func<PackagedFileInfo, bool>? filter = null)
	{
		var task = await Task.Run(async () =>
		{
			// execute actual operation in child task
			var childTask = Task.Factory.StartNew(() =>
			{
				try
				{
					return UncompressPackageContained(pakPath, outputDirectory, filter);
				}
				catch (Exception) { return false; }
			}, TaskCreationOptions.AttachedToParent);

			var awaiter = childTask.GetAwaiter();
			while (!awaiter.IsCompleted)
			{
				await Task.Delay(0, token);
			}
			return childTask.Result;
		}, token);

		return task;
	}

	public static bool WriteTextFile(string path, string contents)
	{
		try
		{
			var buffer = Encoding.UTF8.GetBytes(contents);
			using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, buffer.Length, false);
			fs.Write(buffer, 0, buffer.Length);
			return true;
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error writing file: {ex}");
			return false;
		}
	}

	public static async Task<bool> WriteTextFileAsync(string path, string contents)
	{
		try
		{
			var buffer = Encoding.UTF8.GetBytes(contents);
			using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, buffer.Length, true);
			await fs.WriteAsync(buffer, 0, buffer.Length);
			return true;
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error writing file: {ex}");
			return false;
		}
	}

	/// <summary>
	/// Replaces a file without ever leaving it half-written: the bytes go to a temporary file next to it, which one rename then
	/// swaps in. If backupPath is set, the previous file is copied there first. If verify is set, it gets the temporary file's
	/// path, and the swap only happens when it returns true.
	/// </summary>
	/// <returns>False when verify refused the new file. Throws when writing or swapping fails. In both cases the target is unchanged.</returns>
	public static async Task<bool> WriteFileSafelyAsync(string path, byte[] contents, string backupPath = null, Func<string, Task<bool>> verify = null)
	{
		// A symbolic link stays a link: replace the file it points to (LinkTarget is null for a missing file or a plain one)
		var info = new FileInfo(path);
		if (info.LinkTarget != null) path = info.ResolveLinkTarget(true)?.FullName ?? path;
		// A unique name, so two writes of the same file at once can't use or delete each other's temporary file
		var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
		try
		{
			using (var fs = new System.IO.FileStream(tempPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 4096, true))
			{
				await fs.WriteAsync(contents);
				fs.Flush(true);
			}
			if (verify != null && !await verify(tempPath))
			{
				File.Delete(tempPath);
				return false;
			}
			if (backupPath != null && File.Exists(path))
			{
				// File.Copy keeps a read-only attribute, which would block the next backup
				if (File.Exists(backupPath)) File.SetAttributes(backupPath, FileAttributes.Normal);
				File.Copy(path, backupPath, true);
			}
			File.Move(tempPath, path, true);
			return true;
		}
		catch
		{
			try { File.Delete(tempPath); } catch { }
			throw;
		}
	}

	public static async Task<byte[]> LoadFileAsBytesAsync(string path, CancellationToken token)
	{
		try
		{
			using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
			var result = new byte[file.Length];
			var totalBytesRead = await file.ReadAsync(result.AsMemory(0, (int)file.Length), token);
			return result;
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error writing file: {ex}");
		}
		return null;
	}

	public static async Task<bool> CopyFileAsync(string copyFromPath, string copyToPath, CancellationToken token)
	{
		try
		{
			using var sourceFile = new FileStream(copyFromPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
			using var outputFile = File.Create(copyToPath, 128000, FileOptions.Asynchronous);
			await sourceFile.CopyToAsync(outputFile, 128000, token); // 81920 default
			return true;
		}
		catch (Exception ex)
		{
			DivinityApp.Log($"Error copying file: {ex}");
		}
		return false;
	}

	public static bool TryGetDirectoryOrParent(string path, out string parentDir)
	{
		parentDir = "";
		try
		{
			if (Directory.Exists(path))
			{
				parentDir = path;
				return true;
			}
			var dir = Directory.GetParent(path);
			if (dir != null)
			{
				parentDir = dir.FullName;
				return true;
			}
		}
		catch (Exception ex) { }
		return false;
	}

	public static bool TryGetParent(string path, out string parentDir)
	{
		parentDir = "";
		try
		{
			var dir = Directory.GetParent(path);
			if (dir != null)
			{
				parentDir = dir.FullName;
				return true;
			}
		}
		catch (Exception ex) { }
		return false;
	}

	private static readonly EnumerationOptions _defaultOpts = new() { AttributesToSkip = FileAttributes.Hidden };

	public static IEnumerable<string> EnumerateFiles(string path, EnumerationOptions? opts = null, Func<string, bool>? inclusionFilter = null)
	{
		opts ??= _defaultOpts;
		if (inclusionFilter != null)
		{
			return Directory.EnumerateFiles(path, "*", opts).Where(inclusionFilter);
		}
		return Directory.EnumerateFiles(path, "*", opts);
	}

	public static IEnumerable<string> EnumerateDirectories(string path, EnumerationOptions? opts = null, Func<string, bool>? inclusionFilter = null)
	{
		opts ??= _defaultOpts;
		if (inclusionFilter != null)
		{
			return Directory.EnumerateDirectories(path, "*", opts).Where(inclusionFilter);
		}
		return Directory.EnumerateDirectories(path, "*", opts);
	}

	private static readonly FileSystemRights _readAccessRights = FileSystemRights.Read | FileSystemRights.Synchronize;

	public static bool HasFileReadPermission(params string[] paths)
	{
		foreach (var path in paths)
		{
			try
			{
				if (!String.IsNullOrEmpty(path) && File.Exists(path))
				{
					var info = new FileInfo(path);
					var security = info.GetAccessControl();
					var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
					var rules = security.GetAccessRules(true, true, usersSid.GetType()).OfType<FileSystemAccessRule>();
					if (!rules.Any(r => r.FileSystemRights == _readAccessRights || r.FileSystemRights == FileSystemRights.FullControl))
					{
						DivinityApp.Log($"Lacking permission for file '{path}'");
						return false;
					}
				}
			}
			catch (UnauthorizedAccessException ex)
			{
				DivinityApp.Log($"Lacking permission for file '{path}':\n{ex}");
				return false;
			}
			catch (Exception ex)
			{
				DivinityApp.Log($"Error checking permissions for '{path}':\n{ex}");
			}
		}
		return true;
	}

	public static bool HasDirectoryReadPermission(params string[] paths)
	{
		foreach (var path in paths)
		{
			try
			{
				if (!String.IsNullOrEmpty(path) && Directory.Exists(path))
				{
					var info = new DirectoryInfo(path);
					var security = info.GetAccessControl();
					var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
					var rules = security.GetAccessRules(true, true, usersSid.GetType()).OfType<FileSystemAccessRule>();
					if (!rules.Any(r => r.FileSystemRights == _readAccessRights || r.FileSystemRights == FileSystemRights.FullControl))
					{
						DivinityApp.Log($"Lacking permission for directory '{path}'. Rights({String.Join(";", rules.Select(x => x.FileSystemRights))})");
						return false;
					}
				}
			}
			catch (UnauthorizedAccessException ex)
			{
				DivinityApp.Log($"Lacking permission for directory '{path}':\n{ex}");
				return false;
			}
			catch (Exception ex)
			{
				DivinityApp.Log($"Error checking permissions for '{path}':\n{ex}");
			}
		}
		return true;
	}

	public static string GetParentOrEmpty(string path)
	{
		if(TryGetParent(path, out var parent))
		{
			return parent;
		}
		return string.Empty;
	}
}
