namespace Odysseus.Packages;

using System.Threading;
using System.Threading.Tasks;
using System.Xml;

using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;

/// <summary>
/// Raised when a package could not be obtained or is not usable on this framework.
/// </summary>
public sealed class PackageUnavailableException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What could not be obtained, and from where it was looked for.</param>
	public PackageUnavailableException(string message)
		: base(message)
	{
	}
}

/// <summary>
/// Downloads a package and unpacks what runs on this framework.
/// </summary>
/// <remarks>
/// Small on purpose. The only thing this has to be right about is that what ends up on disk is what the
/// feed served and that the assemblies handed on are the ones built for the framework this process is
/// running on; everything cleverer - transitive closure resolution, project graphs, lock files - belongs
/// to a build, and this is not one.
/// </remarks>
public sealed class PackageFetcher
{
	private readonly PackageSourceSet _sources;
	private readonly PackageCache _cache;
	private readonly NuGetFramework _framework;

	/// <summary>
	/// Creates the fetcher.
	/// </summary>
	/// <param name="sources">Where packages come from.</param>
	/// <param name="cache">Where they are kept.</param>
	/// <param name="targetFramework">Short name of the framework to unpack for, for example <c>net10.0</c>.</param>
	public PackageFetcher(PackageSourceSet sources, PackageCache cache, string targetFramework)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);

		_sources = sources ?? throw new ArgumentNullException(nameof(sources));
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));

		// Either spelling: a short folder name such as net10.0, or the long form the runtime reports for
		// itself, which is what a caller asking "what am I running on" has to hand.
		_framework = targetFramework.Contains(',', StringComparison.Ordinal)
			? NuGetFramework.ParseFrameworkName(targetFramework, DefaultFrameworkNameProvider.Instance)
			: NuGetFramework.Parse(targetFramework);
	}

	/// <summary>Where packages are kept.</summary>
	public PackageCache Cache => _cache;

	/// <summary>
	/// Obtains a package, from the cache when it is already there and from a source otherwise.
	/// </summary>
	/// <param name="id">Package identifier.</param>
	/// <param name="version">Exact version, or empty for the newest release the sources offer.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was obtained.</returns>
	/// <exception cref="PackageUnavailableException">No source offers it, or it carries nothing for this framework.</exception>
	public async Task<FetchedPackage> FetchAsync(string id, string version, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);

		if (!string.IsNullOrWhiteSpace(version) && _cache.Read(id, version) is { } cached)
			return Unpack(id, version, cached);

		var (resolved, content) = await DownloadAsync(id, version, cancellationToken);

		_cache.Write(id, resolved, content);

		return Unpack(id, resolved, content);
	}

	/// <summary>
	/// Reads a package that is already in the cache, without touching a source.
	/// </summary>
	/// <param name="id">Package identifier.</param>
	/// <param name="version">Package version.</param>
	/// <returns>What is on disk, or <see langword="null"/> when that version is not cached.</returns>
	public FetchedPackage Cached(string id, string version)
	{
		var content = _cache.Read(id, version);

		return content is null ? null : Unpack(id, version, content);
	}

	private static NuGetVersion Choose(IEnumerable<NuGetVersion> offered, string version)
	{
		var all = offered?.ToArray() ?? [];

		if (all.Length == 0)
			return null;

		if (string.IsNullOrWhiteSpace(version))
			return all.Where(v => !v.IsPrerelease).OrderBy(v => v).LastOrDefault() ?? all.OrderBy(v => v).Last();

		if (!NuGetVersion.TryParse(version, out var wanted))
			throw new PackageUnavailableException($"'{version}' is not a version a package can have.");

		return all.FirstOrDefault(v => v.Equals(wanted));
	}

	/// <summary>
	/// Writes one file out of the package under a temporary name and moves it into place, so an
	/// interrupted unpack leaves nothing that looks like a complete assembly.
	/// </summary>
	private static void Extract(PackageArchiveReader reader, string item, string path)
	{
		var temporary = path + ".partial";

		using (var entry = reader.GetStream(item))
		using (var file = File.Create(temporary))
		{
			entry.CopyTo(file);
		}

		File.Move(temporary, path, overwrite: true);
	}

	private static string Describe(IEnumerable<string> names)
	{
		var all = names.ToArray();

		return all.Length == 0 ? "nothing" : string.Join(", ", all);
	}

	private async Task<(string Version, byte[] Content)> DownloadAsync(
		string id,
		string version,
		CancellationToken cancellationToken)
	{
		using var context = new SourceCacheContext { NoCache = true, DirectDownload = true };

		var failures = new List<string>();

		foreach (var repository in _sources.Repositories)
		{
			try
			{
				var finder = await repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
				var offered = await finder.GetAllVersionsAsync(id, context, NullLogger.Instance, cancellationToken);

				var chosen = Choose(offered, version);

				if (chosen is null)
					continue;

				using var stream = new MemoryStream();

				var copied = await finder.CopyNupkgToStreamAsync(
					id,
					chosen,
					stream,
					context,
					NullLogger.Instance,
					cancellationToken);

				if (!copied)
					continue;

				return (chosen.ToNormalizedString(), stream.ToArray());
			}
			catch (Exception error) when (error is not OperationCanceledException)
			{
				failures.Add($"{repository.PackageSource.Source}: {error.Message}");
			}
		}

		var wanted = string.IsNullOrWhiteSpace(version) ? "the newest release" : version;

		throw new PackageUnavailableException(
			$"No source offers {id} {wanted}. Looked in {string.Join(", ", _sources.Sources)}." +
			(failures.Count == 0 ? string.Empty : $" Errors: {string.Join("; ", failures)}."));
	}

	/// <summary>
	/// Reads a package file, turning a file that is not one into a refusal.
	/// </summary>
	/// <param name="id">Package identifier.</param>
	/// <param name="version">Package version.</param>
	/// <param name="content">The bytes, from a source or from the cache.</param>
	/// <returns>What the package holds.</returns>
	/// <remarks>
	/// A truncated download, a cache folder somebody edited, or a source that answered with a page of
	/// HTML all arrive here as bytes that are not an archive. Left alone they surface as an
	/// <see cref="InvalidDataException"/>, which nothing above recognises and which reaches the caller
	/// as an internal defect - a server saying it broke, where a package saying it is unreadable is both
	/// the truth and something the caller can act on.
	/// </remarks>
	private FetchedPackage Unpack(string id, string version, byte[] content)
	{
		try
		{
			return ReadPackage(id, version, content);
		}
		catch (Exception error) when (error is InvalidDataException or XmlException or PackagingException)
		{
			throw new PackageUnavailableException(
				$"{id} {version} is not a readable package: {error.Message} The file this server holds for it " +
				$"is {content.Length} bytes and is not an archive it can open. Delete it from the connector " +
				"cache and fetch it again, or name a version the source really offers.");
		}
	}

	private FetchedPackage ReadPackage(string id, string version, byte[] content)
	{
		var folder = _cache.FolderOf(id, version);

		Directory.CreateDirectory(folder);

		using var stream = new MemoryStream(content, writable: false);
		using var reader = new PackageArchiveReader(stream);

		var identity = reader.GetIdentity();
		var groups = reader.GetLibItems().ToArray();
		var reducer = new FrameworkReducer();

		var nearest = reducer.GetNearest(_framework, groups.Select(g => g.TargetFramework))
			?? throw new PackageUnavailableException(
				$"{id} {version} carries nothing that runs on {_framework.GetShortFolderName()}. It offers " +
				$"{Describe(groups.Select(g => g.TargetFramework.GetShortFolderName()))}.");

		var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var item in groups.Where(g => g.TargetFramework.Equals(nearest)).SelectMany(g => g.Items))
		{
			if (!item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				continue;

			var name = Path.GetFileName(item.Replace('\\', '/'));
			var path = Path.Combine(folder, name);

			if (!File.Exists(path))
				Extract(reader, item, path);

			assemblies[Path.GetFileNameWithoutExtension(name)] = path;
		}

		if (assemblies.Count == 0)
		{
			throw new PackageUnavailableException(
				$"{id} {version} holds no assembly for {nearest.GetShortFolderName()}, so there is nothing to load.");
		}

		var dependencies = reader.NuspecReader.GetDependencyGroups().ToArray();
		var nearestDependencies = dependencies.Length == 0
			? null
			: reducer.GetNearest(_framework, dependencies.Select(g => g.TargetFramework));

		PackageRequirement[] requirements = [];

		if (nearestDependencies is not null)
		{
			requirements =
			[
				.. dependencies
					.Where(g => g.TargetFramework.Equals(nearestDependencies))
					.SelectMany(g => g.Packages)
					.Select(p => new PackageRequirement(
						p.Id,
						p.VersionRange?.MinVersion?.ToNormalizedString() ?? string.Empty)),
			];
		}

		return new(
			identity.Id,
			identity.Version.ToNormalizedString(),
			PackageCache.HashOf(content),
			folder,
			assemblies,
			requirements);
	}
}
