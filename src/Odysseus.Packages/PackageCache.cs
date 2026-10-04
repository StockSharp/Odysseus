namespace Odysseus.Packages;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

/// <summary>
/// Where downloaded packages are kept between runs of the server.
/// </summary>
/// <remarks>
/// One folder per package and version, holding the package file itself and the assemblies unpacked out
/// of it. The package file is kept rather than discarded because it is what the hash was taken over: an
/// unpacked folder cannot be checked against anything, and a connector this server will load into its
/// own process is worth being able to check.
/// </remarks>
public sealed class PackageCache
{
	/// <summary>Name of the package file inside a cache folder.</summary>
	public const string PackageFileName = "package.nupkg";

	private readonly string _root;

	/// <summary>
	/// Creates the cache.
	/// </summary>
	/// <param name="root">Directory the folders live under.</param>
	public PackageCache(string root)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		_root = Path.GetFullPath(root);
	}

	/// <summary>Directory the folders live under.</summary>
	public string Root => _root;

	/// <summary>
	/// Where one package and version is kept.
	/// </summary>
	/// <param name="id">Package identifier.</param>
	/// <param name="version">Package version.</param>
	/// <returns>The folder, which need not exist.</returns>
	public string FolderOf(string id, string version)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentException.ThrowIfNullOrWhiteSpace(version);

		return Path.Combine(_root, id.ToLowerInvariant(), version.ToLowerInvariant());
	}

	/// <summary>
	/// Every package and version already on disk.
	/// </summary>
	/// <returns>Pairs of identifier and version, in the order the file system reports them.</returns>
	public IReadOnlyList<(string Id, string Version)> Contents()
	{
		if (!Directory.Exists(_root))
			return [];

		var found = new List<(string, string)>();

		foreach (var package in Directory.EnumerateDirectories(_root))
		{
			foreach (var version in Directory.EnumerateDirectories(package))
			{
				if (File.Exists(Path.Combine(version, PackageFileName)))
					found.Add((Path.GetFileName(package), Path.GetFileName(version)));
			}
		}

		return found;
	}

	/// <summary>
	/// Writes the package file into its folder, replacing whatever was there.
	/// </summary>
	/// <param name="id">Package identifier.</param>
	/// <param name="version">Package version.</param>
	/// <param name="content">The package file.</param>
	/// <returns>Path of the written file.</returns>
	/// <remarks>
	/// Written under a temporary name and moved into place, so that a download interrupted half way
	/// leaves nothing that looks like a complete package.
	/// </remarks>
	public string Write(string id, string version, byte[] content)
	{
		ArgumentNullException.ThrowIfNull(content);

		var folder = FolderOf(id, version);

		Directory.CreateDirectory(folder);

		var path = Path.Combine(folder, PackageFileName);
		var temporary = path + ".partial";

		File.WriteAllBytes(temporary, content);
		File.Move(temporary, path, overwrite: true);

		return path;
	}

	/// <summary>
	/// Reads a cached package file, when one is there.
	/// </summary>
	/// <param name="id">Package identifier.</param>
	/// <param name="version">Package version.</param>
	/// <returns>The bytes, or <see langword="null"/> when the package is not cached.</returns>
	public byte[] Read(string id, string version)
	{
		var path = Path.Combine(FolderOf(id, version), PackageFileName);

		return File.Exists(path) ? File.ReadAllBytes(path) : null;
	}

	/// <summary>
	/// Names the versions of a package that are on disk.
	/// </summary>
	/// <param name="id">Package identifier.</param>
	/// <returns>The versions, newest last in the file system's own order.</returns>
	public IReadOnlyList<string> VersionsOf(string id)
		=> [.. Contents().Where(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)).Select(c => c.Version)];

	/// <summary>
	/// The hash a package file is filed under.
	/// </summary>
	/// <param name="content">The package file.</param>
	/// <returns>Its SHA-256, in lower-case hexadecimal.</returns>
	public static string HashOf(byte[] content)
	{
		ArgumentNullException.ThrowIfNull(content);

		return Convert.ToHexStringLower(SHA256.HashData(content));
	}
}
