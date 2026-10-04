namespace Odysseus.Engine.Tests;

/// <summary>
/// What names a platform build, and what happens when nothing does.
/// </summary>
/// <remarks>
/// A run's fingerprint covers the candidate, the data and the costs, and deliberately not the engine,
/// so this string is the whole of what keeps two platform builds out of one namespace of measurements.
/// Which assemblies go into it is therefore a decision and not a detail, and so is the answer for a
/// deployment that cannot be read: it is an empty identity, which the handshake refuses rather than
/// treats as agreement.
/// </remarks>
[TestClass]
public class EngineIdentityTests : OdysseusTestBase
{
	private string _root;
	private string _folder;

	/// <summary>Creates a folder standing in for a deployment.</summary>
	[TestInitialize]
	public void CreateFolder()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_folder = Folder("deployment");
	}

	/// <summary>Removes everything the test deployed.</summary>
	[TestCleanup]
	public void DeleteFolder()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// The identity is the platform's assemblies, and nothing else the deployment carries.
	/// </summary>
	/// <remarks>
	/// What else lies in a deployment differs between a host and the worker beside it - their own
	/// assemblies, their manifests, the libraries only one of them uses - so anything of it in the
	/// identity would make the two disagree while carrying the same platform.
	/// </remarks>
	[TestMethod]
	public void TheIdentityIsThePlatformAssembliesAndNothingElse()
	{
		Deploy(_folder, "StockSharp.Algo.dll", "the engine");
		Deploy(_folder, "Ecng.Common.dll", "what the engine stands on");

		var platform = EngineIdentity.Of(_folder);

		AreNotEqual(string.Empty, platform, "a deployment holding the platform named no engine.");

		Deploy(_folder, "Odysseus.Engine.dll", "the product itself");
		Deploy(_folder, "Newtonsoft.Json.dll", "somebody else's library");
		Deploy(_folder, "StockSharp.Algo.xml", "the documentation of an assembly, which is not the assembly");
		Deploy(_folder, "app.deps.json", "{}");

		AreEqual(platform, EngineIdentity.Of(_folder),
			"something that is not an assembly of the platform changed which engine the deployment names.");
	}

	/// <summary>
	/// Two builds of a platform assembly are two different engines, whatever version they declare.
	/// </summary>
	/// <remarks>
	/// The platform is built from source, where every build declares the same version, so the content
	/// of an assembly is the only thing that tells one build from the next.
	///
	/// The two name prefixes are restated here rather than read from the production list, because the
	/// list is the decision under test: a prefix added to it re-identifies every deployment, and a
	/// prefix dropped from it lets two builds that run a candidate differently agree that they are the
	/// same.
	/// </remarks>
	/// <param name="assembly">An assembly of the platform.</param>
	[TestMethod]
	[DataRow("StockSharp.Algo.dll")]
	[DataRow("Ecng.Common.dll")]
	public void AnotherBuildOfAPlatformAssemblyIsADifferentEngine(string assembly)
	{
		Deploy(_folder, "StockSharp.Messages.dll", "the same in both");
		Deploy(_folder, assembly, "one build");

		var first = EngineIdentity.Of(_folder);

		Deploy(_folder, assembly, "another build");

		AreNotEqual(first, EngineIdentity.Of(_folder),
			$"a deployment carrying another build of {assembly} shares its predecessor's identity.");
	}

	/// <summary>
	/// The same assemblies in another folder are the same engine, which is what lets a host recognise
	/// the worker deployed beside it.
	/// </summary>
	[TestMethod]
	public void TheSameAssembliesElsewhereAreTheSameEngine()
	{
		var elsewhere = Folder("elsewhere");

		// Written in the opposite order, which is the order a folder may well list them in.
		Deploy(_folder, "StockSharp.Algo.dll", "the engine");
		Deploy(_folder, "Ecng.Common.dll", "what the engine stands on");
		Deploy(elsewhere, "Ecng.Common.dll", "what the engine stands on");
		Deploy(elsewhere, "StockSharp.Algo.dll", "the engine");

		var here = EngineIdentity.Of(_folder);

		AreNotEqual(string.Empty, here, "a deployment holding the platform named no engine.");
		AreEqual(here, EngineIdentity.Of(elsewhere));
	}

	/// <summary>A deployment lacking a platform assembly another one holds is a different engine.</summary>
	[TestMethod]
	public void AMissingPlatformAssemblyIsADifferentEngine()
	{
		Deploy(_folder, "StockSharp.Algo.dll", "the engine");

		var without = EngineIdentity.Of(_folder);

		Deploy(_folder, "StockSharp.Messages.dll", "what the engine speaks");

		AreNotEqual(without, EngineIdentity.Of(_folder),
			"a deployment that gained a platform assembly kept the identity it had without it.");
	}

	/// <summary>
	/// What an assembly holds is tied to its name: the same bytes divided between two assemblies in
	/// another way are another engine.
	/// </summary>
	[TestMethod]
	public void BytesMovedBetweenAssembliesAreADifferentEngine()
	{
		var elsewhere = Folder("elsewhere");

		Deploy(_folder, "StockSharp.Algo.dll", "ab");
		Deploy(_folder, "StockSharp.Messages.dll", "c");
		Deploy(elsewhere, "StockSharp.Algo.dll", "a");
		Deploy(elsewhere, "StockSharp.Messages.dll", "bc");

		AreNotEqual(EngineIdentity.Of(_folder), EngineIdentity.Of(elsewhere));
	}

	/// <summary>
	/// A deployment that says nothing about the platform names no engine, rather than being guessed at.
	/// </summary>
	/// <remarks>
	/// The empty answer is what makes the refusal in the handshake possible; a plausible-looking
	/// substitute here would be indistinguishable from a real identity everywhere it is used.
	/// </remarks>
	[TestMethod]
	public void ADeploymentThatSaysNothingNamesNoEngine()
	{
		AreEqual(string.Empty, EngineIdentity.Of(_folder), "an empty folder named an engine.");

		AreEqual(string.Empty, EngineIdentity.Of(Path.Combine(_folder, "not-there")),
			"a folder that is not there named an engine.");
	}

	/// <summary>
	/// A manifest naming the platform is not the platform: a deployment holding no assembly of it names
	/// no engine, whatever its manifest lists.
	/// </summary>
	[TestMethod]
	public void ADeploymentWithoutThePlatformNamesNoEngine()
	{
		Deploy(_folder, "Odysseus.Engine.dll", "the product itself");
		Deploy(_folder, "app.deps.json", """{"libraries":{"StockSharp.Algo/5.0.0":{"type":"project"}}}""");

		AreEqual(string.Empty, EngineIdentity.Of(_folder));
	}

	private static void Deploy(string folder, string name, string content)
		=> File.WriteAllText(Path.Combine(folder, name), content);

	private string Folder(string name)
	{
		var folder = Path.Combine(_root, name);

		Directory.CreateDirectory(folder);

		return folder;
	}
}
