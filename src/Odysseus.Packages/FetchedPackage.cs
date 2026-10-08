namespace StockSharp.Odysseus.Packages;

/// <summary>
/// One package this package depends on, and the lowest version it will accept.
/// </summary>
/// <param name="Id">Package identifier.</param>
/// <param name="MinimumVersion">Lowest version the dependency range allows, or an empty string when it has no floor.</param>
public sealed record PackageRequirement(string Id, string MinimumVersion);

/// <summary>
/// A package this server downloaded and unpacked.
/// </summary>
/// <param name="Id">Package identifier, as the package declares it.</param>
/// <param name="Version">Version that was resolved.</param>
/// <param name="Sha256">Hash of the package file, which is what an audit entry records.</param>
/// <param name="CacheDirectory">Where it was unpacked.</param>
/// <param name="Assemblies">Simple assembly name to file path, for the target framework this server runs on.</param>
/// <param name="Requirements">What the package says it needs, so a conflict is refused before anything is loaded.</param>
public sealed record FetchedPackage(
	string Id,
	string Version,
	string Sha256,
	string CacheDirectory,
	IReadOnlyDictionary<string, string> Assemblies,
	IReadOnlyList<PackageRequirement> Requirements);
