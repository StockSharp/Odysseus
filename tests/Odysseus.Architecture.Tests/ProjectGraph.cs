namespace StockSharp.Odysseus.Architecture.Tests;

using System.Xml.Linq;

/// <summary>
/// One project of the solution as it is declared on disk, not as it is loaded at runtime:
/// the dependency rules must hold before anything is compiled.
/// </summary>
public sealed class ProjectInfo
{
	/// <summary>Assembly name, for example <c>Odysseus.Domain</c>.</summary>
	public string Name { get; init; }

	/// <summary>Absolute path of the project file.</summary>
	public string Path { get; init; }

	/// <summary>Names of the projects referenced directly, whether linked or ordered only.</summary>
	public IReadOnlyCollection<string> ProjectReferences { get; init; }

	/// <summary>
	/// Names of the projects whose output this one links. A reference carrying
	/// <c>ReferenceOutputAssembly="false"</c> only orders the build and is absent here.
	/// </summary>
	public IReadOnlyCollection<string> LinkedProjectReferences { get; init; }

	/// <summary>
	/// Absolute paths of the projects referenced directly, resolved against this project's folder, so
	/// that a reference reaching outside the repository is visible as a path rather than as a name.
	/// The trading platform's projects are not among them: see <see cref="PlatformReferences"/>.
	/// </summary>
	public IReadOnlyCollection<string> ProjectReferencePaths { get; init; }

	/// <summary>
	/// Names of the trading platform's projects referenced directly. They are built out of the checkout
	/// the <c>StockSharpSource</c> property names, so where one lies is decided by the build and is not
	/// a path this can resolve - only the fact that the reference goes through that property.
	/// </summary>
	public IReadOnlyCollection<string> PlatformReferences { get; init; }

	/// <summary>Package identifiers referenced directly.</summary>
	public IReadOnlyCollection<string> PackageReferences { get; init; }

	/// <summary>Whether the project builds a process of its own rather than a library.</summary>
	public bool IsExecutable { get; init; }

	/// <summary>Raw text of the project file, for property assertions.</summary>
	public string Text { get; init; }
}

/// <summary>
/// Reads every project of the solution so that dependency rules can be asserted against the
/// declared graph.
/// </summary>
public static class ProjectGraph
{
	/// <summary>How a project file spells the checkout the trading platform is built from.</summary>
	private const string PlatformSource = "$(StockSharpSource)";

	/// <summary>
	/// Loads the projects under the given repository-relative folder.
	/// </summary>
	/// <param name="repositoryRoot">Directory holding <c>Odysseus.slnx</c>.</param>
	/// <param name="folder">Folder to scan, for example <c>src</c>.</param>
	/// <returns>Projects keyed by assembly name.</returns>
	public static IReadOnlyDictionary<string, ProjectInfo> Load(string repositoryRoot, string folder)
	{
		if (repositoryRoot is null)
			throw new ArgumentNullException(nameof(repositoryRoot));

		if (folder is null)
			throw new ArgumentNullException(nameof(folder));

		var root = Path.Combine(repositoryRoot, folder);
		var result = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

		foreach (var file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
		{
			var document = XDocument.Load(file);
			var directory = Path.GetDirectoryName(file);

			var references = document
				.Descendants("ProjectReference")
				.Select(e => new { Include = (string)e.Attribute("Include"), Linked = LinksOutput(e) })
				.Where(r => !string.IsNullOrEmpty(r.Include))
				.ToArray();

			var packageRefs = document
				.Descendants("PackageReference")
				.Select(e => (string)e.Attribute("Include"))
				.Where(v => !string.IsNullOrEmpty(v))
				.ToArray();

			var name = Path.GetFileNameWithoutExtension(file);

			result.Add(name, new ProjectInfo
			{
				Name = name,
				Path = file,
				ProjectReferences = references.Select(r => NameOf(r.Include)).ToArray(),
				LinkedProjectReferences = references.Where(r => r.Linked).Select(r => NameOf(r.Include)).ToArray(),
				ProjectReferencePaths = references
					.Where(r => !ReachesThePlatform(r.Include))
					.Select(r => Resolve(directory, r.Include))
					.ToArray(),
				PlatformReferences = references
					.Where(r => ReachesThePlatform(r.Include))
					.Select(r => NameOf(r.Include))
					.ToArray(),
				PackageReferences = packageRefs,
				IsExecutable = document
					.Descendants("OutputType")
					.Any(e => IsExecutableOutput(e.Value)),
				Text = File.ReadAllText(file),
			});
		}

		return result;
	}

	/// <summary>
	/// Whether a project reference brings the referenced output into the compilation. MSBuild accepts
	/// <c>ReferenceOutputAssembly</c> as an attribute or as a child element, and either spelling turns
	/// the reference into a build-order edge only.
	/// </summary>
	private static bool LinksOutput(XElement reference)
	{
		var value = (string)reference.Attribute("ReferenceOutputAssembly")
			?? (string)reference.Element("ReferenceOutputAssembly");

		return !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>Whether an <c>OutputType</c> value describes a process rather than a library.</summary>
	private static bool IsExecutableOutput(string value)
	{
		var trimmed = value.Trim();

		return string.Equals(trimmed, "Exe", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(trimmed, "WinExe", StringComparison.OrdinalIgnoreCase);
	}

	private static bool ReachesThePlatform(string include)
		=> include.StartsWith(PlatformSource, StringComparison.Ordinal);

	private static string NameOf(string include)
		=> Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));

	private static string Resolve(string directory, string include)
		=> Path.GetFullPath(Path.Combine(directory, include.Replace('\\', '/')));
}
