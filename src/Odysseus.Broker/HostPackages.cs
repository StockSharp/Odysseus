namespace Odysseus.Broker;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using NuGet.Versioning;

using Odysseus.Application;
using Odysseus.Packages;

/// <summary>
/// Which versions of the platform's packages this server carries.
/// </summary>
/// <remarks>
/// Needed because a version conflict with a connector cannot announce itself. Every assembly the trading
/// platform publishes carries the same assembly version whatever the package version is, and none is
/// strong-named, so the runtime unifies two builds silently and the disagreement surfaces as a missing
/// method somewhere inside connecting - which is the worst moment to find out.
///
/// Read from the deployment's own dependency manifests, which are the only place the mapping from a
/// package version to a loaded assembly survives a build.
///
/// Only what the deployment took as a package is here. The trading platform itself is built from
/// source and has no package version, so what a connector asks of it is not compared: the manifest
/// lists such an assembly under the version every build from source declares.
/// </remarks>
internal sealed class HostPackages
{
	private readonly IReadOnlyDictionary<string, NuGetVersion> _versions;

	private HostPackages(IReadOnlyDictionary<string, NuGetVersion> versions)
	{
		_versions = versions;
	}

	/// <summary>What was found, by package identifier.</summary>
	public IReadOnlyDictionary<string, NuGetVersion> Versions => _versions;

	/// <summary>
	/// Reads the manifests beside a running host.
	/// </summary>
	/// <param name="directory">Folder holding the host's <c>.deps.json</c> files.</param>
	/// <returns>The packages, or an empty table when nothing could be read.</returns>
	public static HostPackages Read(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);

		var versions = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);

		if (!Directory.Exists(directory))
			return new(versions);

		foreach (var file in Directory.EnumerateFiles(directory, "*.deps.json"))
		{
			foreach (var (id, version) in ReadOne(file))
			{
				if (!versions.TryGetValue(id, out var known) || version > known)
					versions[id] = version;
			}
		}

		return new(versions);
	}

	/// <summary>
	/// Refuses a package that wants a newer platform than this server carries.
	/// </summary>
	/// <param name="package">The connector package that was fetched.</param>
	/// <exception cref="ConnectorRefusedException">It wants a version this server does not have.</exception>
	/// <remarks>
	/// Only the platform's own packages are compared. Everything else the connector brings is loaded out
	/// of its own folder and does not have to agree with anything here.
	/// </remarks>
	public void AssertSatisfies(FetchedPackage package)
	{
		ArgumentNullException.ThrowIfNull(package);

		foreach (var requirement in package.Requirements)
		{
			if (!IsPlatform(requirement.Id))
				continue;

			if (!_versions.TryGetValue(requirement.Id, out var present))
				continue;

			if (string.IsNullOrWhiteSpace(requirement.MinimumVersion))
				continue;

			if (!NuGetVersion.TryParse(requirement.MinimumVersion, out var wanted))
				continue;

			if (wanted <= present)
				continue;

			throw new ConnectorRefusedException(
				$"{package.Id} {package.Version} needs {requirement.Id} {requirement.MinimumVersion} and this " +
				$"server carries {present}. A connector runs on the platform the server already loaded, so it " +
				"cannot bring a newer one with it. Upgrade this server, or choose an older build of the connector.");
		}
	}

	/// <summary>
	/// Whether a package is part of the platform the host already carries.
	/// </summary>
	/// <param name="id">Package identifier.</param>
	/// <returns>Whether the host owns it.</returns>
	public static bool IsPlatform(string id)
		=> id is not null &&
			(id.StartsWith("StockSharp.", StringComparison.OrdinalIgnoreCase) ||
				id.StartsWith("Ecng.", StringComparison.OrdinalIgnoreCase));

	private static IReadOnlyList<(string Id, NuGetVersion Version)> ReadOne(string file)
	{
		var found = new List<(string, NuGetVersion)>();

		JsonDocument document;

		try
		{
			document = JsonDocument.Parse(File.ReadAllBytes(file));
		}
		catch (JsonException)
		{
			// A manifest that cannot be parsed says nothing about what is loaded, and refusing to start
			// over it would be worse than not knowing.
			return found;
		}

		using (document)
		{
			if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
				libraries.ValueKind != JsonValueKind.Object)
			{
				return found;
			}

			foreach (var library in libraries.EnumerateObject())
			{
				if (!IsPackage(library.Value))
					continue;

				var slash = library.Name.LastIndexOf('/');

				if (slash <= 0)
					continue;

				if (NuGetVersion.TryParse(library.Name[(slash + 1)..], out var version))
					found.Add((library.Name[..slash], version));
			}
		}

		return found;
	}

	private static bool IsPackage(JsonElement library)
		=> library.ValueKind == JsonValueKind.Object &&
			library.TryGetProperty("type", out var type) &&
			type.ValueKind == JsonValueKind.String &&
			string.Equals(type.GetString(), "package", StringComparison.Ordinal);
}
