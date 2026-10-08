namespace StockSharp.Odysseus.Packages;

using NuGet.Protocol;

/// <summary>
/// Where packages are downloaded from, stated rather than discovered.
/// </summary>
/// <remarks>
/// Deliberately not <c>Settings.LoadDefaultSettings</c>. The repository carries no <c>NuGet.config</c>
/// on purpose, and the machine's must not leak in: a server that downloads and then loads code has to
/// say out loud where that code came from, and "wherever this machine happens to be configured to look"
/// is not saying it.
///
/// A plain directory is a source too, which is what makes the download path testable without a network.
/// </remarks>
public sealed class PackageSourceSet
{
	/// <summary>The public gallery, which is where a connector normally comes from.</summary>
	public const string PublicGallery = "https://api.nuget.org/v3/index.json";

	private readonly SourceRepository[] _repositories;

	/// <summary>
	/// Creates the set.
	/// </summary>
	/// <param name="sources">Feed addresses or local directories, in the order they are to be tried.</param>
	public PackageSourceSet(IEnumerable<string> sources)
	{
		ArgumentNullException.ThrowIfNull(sources);

		var named = sources
			.Where(s => !string.IsNullOrWhiteSpace(s))
			.Select(s => s.Trim())
			.ToArray();

		if (named.Length == 0)
			named = [PublicGallery];

		Sources = named;

		_repositories = [.. named.Select(s => Repository.Factory.GetCoreV3(s))];
	}

	/// <summary>The sources as they were named.</summary>
	public IReadOnlyList<string> Sources { get; }

	/// <summary>The sources as repositories, in the order they are tried.</summary>
	internal IReadOnlyList<SourceRepository> Repositories => _repositories;

	/// <summary>
	/// Reads the set from a delimited list, falling back to the public gallery.
	/// </summary>
	/// <param name="list">Sources separated by semicolons or commas, or null.</param>
	/// <returns>The set.</returns>
	public static PackageSourceSet Parse(string list)
	{
		if (string.IsNullOrWhiteSpace(list))
			return new([PublicGallery]);

		return new(list.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
	}
}
