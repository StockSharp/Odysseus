namespace Odysseus.Engine;

using System.Linq;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Which build of the trading platform a deployment carries.
/// </summary>
/// <remarks>
/// A run's fingerprint covers the candidate, the data and the costs, and deliberately not the engine:
/// adding a field to it would re-identify every run this product has ever recorded. That is safe only
/// while the thing that runs a candidate is the same build as the thing that recorded it, which stopped
/// being automatic the moment the runner became a second executable. So the two are compared once, at
/// the handshake, and a worker that differs is refused before it runs anything - rather than after,
/// when two runs with the same fingerprint would have been produced by two different engines and the
/// cache would hand back the wrong one.
///
/// Read from the platform's assemblies themselves. The platform is built from source, where every
/// build declares the same version, so what an assembly holds is the only thing that tells one build
/// from the next.
///
/// Reading is all this does. A deployment it can say nothing about comes back as an empty identity,
/// which is not a wildcard and is not a match with another empty one: what to do about not knowing is
/// decided where the guarantee is wanted, in <see cref="WorkerHost"/>, and there it is a refusal.
/// </remarks>
public static class EngineIdentity
{
	/// <summary>What the name of every assembly of the platform starts with.</summary>
	private static readonly string[] _platform =
	[
		"StockSharp.",
		"Ecng.",
	];

	/// <summary>
	/// The identity of the deployment in a folder.
	/// </summary>
	/// <param name="directory">Folder holding the deployment's assemblies.</param>
	/// <returns>The identity, or an empty string when the folder holds no assembly of the platform.</returns>
	public static string Of(string directory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);

		if (!Directory.Exists(directory))
			return string.Empty;

		var assemblies = Directory
			.EnumerateFiles(directory, "*.dll")
			.Where(f => _platform.Any(p => Path.GetFileName(f).StartsWith(p, StringComparison.OrdinalIgnoreCase)))
			.OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
			.ToArray();

		if (assemblies.Length == 0)
			return string.Empty;

		using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		foreach (var assembly in assemblies)
		{
			// A name and a content, each as a digest of one length: where one assembly ends and the next
			// begins is then part of what is hashed.
			identity.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(assembly))));

			using var content = File.OpenRead(assembly);

			identity.AppendData(SHA256.HashData(content));
		}

		return Convert.ToHexStringLower(identity.GetHashAndReset());
	}

	/// <summary>
	/// The identity of the process asking.
	/// </summary>
	/// <returns>The identity.</returns>
	public static string Current() => Of(AppContext.BaseDirectory);
}
