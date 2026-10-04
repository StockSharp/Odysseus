namespace Odysseus.Broker;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;

/// <summary>
/// Where a downloaded connector's assemblies are loaded.
/// </summary>
/// <remarks>
/// Only what the package brought resolves here; everything else - the platform, the base class library,
/// this product - returns null and is served by the default context. That is not an optimisation, it is
/// the correctness condition: an adapter loaded against its own copy of the platform's message types
/// would be a different type from the host's despite the identical name, and adding it to a connector
/// would fail with a cast error that reads like nonsense.
///
/// Not collectible, and there is no point pretending otherwise. An adapter is a log receiver with timers
/// and live connections, and the platform's own type cache is a process-wide dictionary keyed by type
/// with no eviction, so one conversion pins the context for the life of the process. One connector per
/// server lifetime is the honest scope; selecting a second one loads a second context and leaves the
/// first, which is why the tool that does it says so.
/// </remarks>
internal sealed class ConnectorLoadContext : AssemblyLoadContext
{
	private readonly IReadOnlyDictionary<string, string> _assemblies;

	/// <summary>
	/// Creates the context.
	/// </summary>
	/// <param name="name">Name it appears under in a diagnostic.</param>
	/// <param name="assemblies">Simple assembly name to file path, from the package that was unpacked.</param>
	public ConnectorLoadContext(string name, IReadOnlyDictionary<string, string> assemblies)
		: base(name, isCollectible: false)
	{
		_assemblies = assemblies ?? throw new ArgumentNullException(nameof(assemblies));
	}

	/// <inheritdoc />
	protected override Assembly Load(AssemblyName assemblyName)
	{
		ArgumentNullException.ThrowIfNull(assemblyName);

		return _assemblies.TryGetValue(assemblyName.Name, out var path) ? LoadFromAssemblyPath(path) : null;
	}
}
