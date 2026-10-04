namespace Odysseus.TestKit;

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

/// <summary>
/// Builds a real NuGet package in memory, so that everything which reads one can be driven offline.
/// </summary>
/// <remarks>
/// A plain directory is a package source as far as the protocol is concerned, so a file written into a
/// temporary folder is a feed and the whole download path runs against it without a network. The
/// assembly inside is not an assembly - nothing that uses this loads it - but the package is a real
/// one, so what is read out of it is what a connector's package would give.
/// </remarks>
public static class FixturePackage
{
	/// <summary>Framework folder the assembly is placed under, which is the one this product runs on.</summary>
	public const string Framework = "net10.0";

	/// <summary>
	/// Builds a package holding one assembly and, when one is named, one dependency.
	/// </summary>
	/// <param name="id">Package identifier.</param>
	/// <param name="version">Package version.</param>
	/// <param name="dependencyId">Package it depends on, or an empty string for a package that depends on nothing.</param>
	/// <param name="dependencyVersion">Lowest version of that dependency it accepts.</param>
	/// <returns>The package file.</returns>
	public static byte[] Bytes(string id, string version, string dependencyId, string dependencyVersion)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		ArgumentException.ThrowIfNullOrWhiteSpace(version);

		using var buffer = new MemoryStream();

		using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			Write(archive, $"{id}.nuspec", Nuspec(id, version, dependencyId, dependencyVersion));
			Write(archive, $"lib/{Framework}/{id}.dll", "not an assembly, and nothing that reads this loads it");
			Write(archive, $"lib/{Framework}/{id}.xml", "<doc />");
		}

		return buffer.ToArray();
	}

	/// <summary>
	/// Writes such a package into a directory that is to be read as a feed.
	/// </summary>
	/// <param name="feed">Directory to write into; it is created when it is not there.</param>
	/// <param name="id">Package identifier.</param>
	/// <param name="version">Package version.</param>
	/// <param name="dependencyId">Package it depends on, or an empty string for a package that depends on nothing.</param>
	/// <param name="dependencyVersion">Lowest version of that dependency it accepts.</param>
	/// <returns>Path of the file that was written.</returns>
	public static string WriteTo(string feed, string id, string version, string dependencyId, string dependencyVersion)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(feed);

		Directory.CreateDirectory(feed);

		var path = Path.Combine(feed, $"{id}.{version}.nupkg");

		File.WriteAllBytes(path, Bytes(id, version, dependencyId, dependencyVersion));

		return path;
	}

	private static void Write(ZipArchive archive, string path, string content)
	{
		using var entry = archive.CreateEntry(path).Open();

		entry.Write(Encoding.UTF8.GetBytes(content));
	}

	private static string Nuspec(string id, string version, string dependencyId, string dependencyVersion)
	{
		var dependencies = string.IsNullOrWhiteSpace(dependencyId)
			? $"""
				    <dependencies>
				      <group targetFramework="{Framework}" />
				    </dependencies>
				"""
			: $"""
				    <dependencies>
				      <group targetFramework="{Framework}">
				        <dependency id="{dependencyId}" version="{dependencyVersion}" />
				      </group>
				    </dependencies>
				""";

		return $"""
			<?xml version="1.0" encoding="utf-8"?>
			<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
			  <metadata>
			    <id>{id}</id>
			    <version>{version}</version>
			    <authors>Odysseus</authors>
			    <description>A package a test made, so that reading one can be exercised without a network.</description>
			{dependencies}
			  </metadata>
			</package>
			""";
	}
}
