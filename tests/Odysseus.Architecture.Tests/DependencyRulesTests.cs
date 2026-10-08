namespace StockSharp.Odysseus.Architecture.Tests;

using StockSharp.Odysseus.TestKit;

/// <summary>
/// Enforces the layering: the domain knows nothing, the application layer reaches technology through
/// ports only, every third-party API is visible in exactly one project, and a process boundary stays a
/// process boundary. The rules read the project files as they are declared on disk, so a violation is
/// caught before anything is compiled.
/// </summary>
[TestClass]
public class DependencyRulesTests : OdysseusTestBase
{
	/// <summary>The one project the trading platform is visible in.</summary>
	private const string PlatformProject = "Odysseus.Platform";

	/// <summary>
	/// The projects of the StockSharp repository the product is compiled against. Nothing else that
	/// repository holds is, and nothing published under its name: that is a broker connector, which is
	/// chosen and loaded while the product runs.
	/// </summary>
	private static readonly string[] _platformKernel =
	[
		"Algo.Strategies",
		"Algo.Indicators",
	];

	/// <summary>The projects the application layer may name.</summary>
	private static readonly string[] _applicationMayReference =
	[
		"Odysseus.Domain",
		"Odysseus.Spec",
		"Odysseus.Evaluation",
	];

	private static IReadOnlyDictionary<string, ProjectInfo> _source;

	private static IReadOnlyDictionary<string, ProjectInfo> _tests;

	private static IReadOnlyDictionary<string, ProjectInfo> Source => _source ??= ProjectGraph.Load(RepositoryRoot, "src");

	private static IReadOnlyDictionary<string, ProjectInfo> Tests => _tests ??= ProjectGraph.Load(RepositoryRoot, "tests");

	private static IEnumerable<ProjectInfo> EveryProject => Source.Values.Concat(Tests.Values);

	/// <summary>The domain depends on nothing at all.</summary>
	[TestMethod]
	public void DomainHasNoDependencies()
	{
		var domain = Source["Odysseus.Domain"];

		IsTrue(domain.ProjectReferences.Count == 0,
			$"Odysseus.Domain must not reference other projects, found: {Join(domain.ProjectReferences)}");

		IsTrue(domain.PackageReferences.Count == 0,
			$"Odysseus.Domain must not reference packages, found: {Join(domain.PackageReferences)}");
	}

	/// <summary>
	/// The application layer holds the ports and the use cases. It may name the three projects that
	/// carry the product's own vocabulary, and no package at all: a port typed against a vendor's API
	/// would need that vendor's package declared here, so the empty package list is what keeps the
	/// ports free of the technologies behind them.
	/// </summary>
	[TestMethod]
	public void ApplicationDoesNotDependOnInfrastructure()
	{
		var application = Source["Odysseus.Application"];

		foreach (var reference in application.ProjectReferences)
		{
			IsTrue(_applicationMayReference.Contains(reference, StringComparer.OrdinalIgnoreCase),
				$"Odysseus.Application references {reference}; it may reference only {Join(_applicationMayReference)}, " +
				"because infrastructure is reached through ports.");
		}

		IsTrue(application.PackageReferences.Count == 0,
			$"Odysseus.Application references {Join(application.PackageReferences)}; a port that named a type from a " +
			"package would tie the use cases to the technology behind it.");
	}

	/// <summary>
	/// The trading platform is visible in exactly one project: a use case, a host or a test that named
	/// a StockSharp type would have to reference the platform's sources, and this is where that shows
	/// up. Everything else reaches the platform through that project, which is also what keeps the
	/// reference set every generated candidate is compiled against from varying by deployment.
	/// </summary>
	[TestMethod]
	public void OnlyThePlatformProjectReferencesStockSharp()
	{
		foreach (var project in EveryProject.Where(p => p.Name != PlatformProject))
		{
			IsTrue(project.PlatformReferences.Count == 0,
				$"{project.Name} references {Join(project.PlatformReferences)} from the StockSharp sources; only " +
				$"{PlatformProject} may depend on the trading platform.");
		}

		// Stated, so that the rule cannot hold merely because nothing is seen to reference the platform.
		var kernel = Source[PlatformProject].PlatformReferences;

		IsTrue(_platformKernel.All(k => kernel.Contains(k, StringComparer.OrdinalIgnoreCase)),
			$"{PlatformProject} references {Join(kernel)} from the StockSharp sources, and the kernel the product " +
			$"is compiled against is {Join(_platformKernel)}.");
	}

	/// <summary>
	/// Downloading a package is a job with one owner. It is the only thing in this product that fetches
	/// bytes and then runs them, so the machinery for it stays where it can be read in one sitting.
	/// </summary>
	[TestMethod]
	public void OnlyPackagesProjectReferencesNuGet()
		=> AssertPackagePrefixIsExclusiveTo("NuGet", "Odysseus.Packages");

	/// <summary>Roslyn is visible in exactly one project.</summary>
	[TestMethod]
	public void OnlyCompilerReferencesRoslyn()
		=> AssertPackagePrefixIsExclusiveTo("Microsoft.CodeAnalysis", "Odysseus.Compiler");

	/// <summary>The database is visible in exactly one project as well.</summary>
	[TestMethod]
	public void OnlyPersistenceReferencesTheDatabase()
	{
		AssertPackagePrefixIsExclusiveTo("Microsoft.Data.Sqlite", "Odysseus.Persistence");
		AssertPackagePrefixIsExclusiveTo("SQLitePCLRaw", "Odysseus.Persistence");
	}

	/// <summary>
	/// A broker connector is named by the operator and loaded while the product runs, so no project may
	/// be compiled against one: the product is built against the platform kernel and nothing else. The
	/// consequence of getting this wrong is not only a hard-wired broker. Whatever a project references
	/// lands in the output folder that supplies the reference set every generated candidate is compiled
	/// against, so a connector on that list would let a candidate name broker types, and would make the
	/// set a candidate was compiled against depend on which connector the deployment happened to build.
	/// </summary>
	[TestMethod]
	public void NoProjectCompilesAgainstABrokerConnector()
	{
		foreach (var project in EveryProject)
		{
			foreach (var reference in project.PlatformReferences)
			{
				IsTrue(_platformKernel.Contains(reference, StringComparer.OrdinalIgnoreCase),
					$"{project.Name} references {reference} from the StockSharp sources. The kernel the product may " +
					$"name is {Join(_platformKernel)}; a connector is loaded at run time, never compiled against.");
			}

			// The platform is built from source, so a package published under its name can only be a connector.
			var packages = project.PackageReferences.Where(IsStockSharpPackage).ToArray();

			IsTrue(packages.Length == 0,
				$"{project.Name} references {Join(packages)}; connectors are loaded at run time, never compiled " +
				"against, and the platform itself is built from source rather than taken as a package.");
		}
	}

	/// <summary>
	/// An executable is a process, and a project that links one has turned another process into a
	/// library reference, which is how work meant to run somewhere else ends up running in the host
	/// after all. Making a host build the executable it later launches is a different thing and stays
	/// allowed: such a reference carries <c>ReferenceOutputAssembly="false"</c> and links nothing. A
	/// test project may link the executable it is the test project of.
	/// </summary>
	[TestMethod]
	public void NoProjectLinksAnExecutable()
	{
		foreach (var project in EveryProject)
		{
			foreach (var reference in project.LinkedProjectReferences.Where(BuildsAnExecutable))
			{
				IsTrue(string.Equals(project.Name, reference + ".Tests", StringComparison.OrdinalIgnoreCase),
					$"{project.Name} links the executable {reference}; another process is reached by path, not by " +
					"reference. Add ReferenceOutputAssembly=\"false\" if the intent is only to have it built.");
			}
		}
	}

	/// <summary>
	/// The trading platform is the one dependency built from source, out of the checkout the build is
	/// pointed at. Everything else the product does not build itself arrives as a published package: a
	/// project reference that leaves the repository by a path of its own would bind the build to a
	/// checkout only one machine has.
	/// </summary>
	[TestMethod]
	public void OnlyThePlatformIsBuiltFromSourcesOutsideTheRepository()
	{
		var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RepositoryRoot)) + Path.DirectorySeparatorChar;

		foreach (var project in EveryProject)
		{
			foreach (var path in project.ProjectReferencePaths)
			{
				IsTrue(path.StartsWith(root, StringComparison.OrdinalIgnoreCase),
					$"{project.Name} references {path}, which is outside the repository; a dependency other than the " +
					"trading platform is consumed as a package.");
			}
		}
	}

	/// <summary>
	/// Nullable reference types are off across the product by convention; a project that turns them
	/// back on locally would silently diverge from the rest of the code base.
	/// </summary>
	[TestMethod]
	public void NoProjectEnablesNullableReferenceTypes()
	{
		foreach (var project in EveryProject)
		{
			IsFalse(project.Text.Contains("<Nullable>enable</Nullable>", StringComparison.OrdinalIgnoreCase),
				$"{project.Name} must not enable nullable reference types; the setting is centralised in Directory.Build.props.");
		}
	}

	/// <summary>
	/// Package versions are centralised, so a version pinned inside a project file is a drift that
	/// central package management would otherwise reject only at restore time.
	/// </summary>
	[TestMethod]
	public void NoProjectPinsPackageVersionLocally()
	{
		foreach (var project in EveryProject)
		{
			IsFalse(project.Text.Contains("<PackageReference", StringComparison.Ordinal) &&
				project.Text.Contains("Version=", StringComparison.Ordinal),
				$"{project.Name} pins a package version locally; versions belong to Directory.Packages.props.");
		}
	}

	private static bool IsStockSharpPackage(string package)
		=> package.StartsWith("StockSharp.", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(package, "StockSharp", StringComparison.OrdinalIgnoreCase);

	private static bool BuildsAnExecutable(string name)
		=> Source.TryGetValue(name, out var project) && project.IsExecutable;

	private static void AssertPackagePrefixIsExclusiveTo(string prefix, string owner)
	{
		foreach (var project in EveryProject)
		{
			var matches = project.PackageReferences
				.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				.ToArray();

			if (matches.Length == 0)
				continue;

			IsTrue(project.Name == owner,
				$"{project.Name} references {Join(matches)}; only {owner} may depend on {prefix}.");
		}
	}

	private static string Join(IEnumerable<string> values)
		=> string.Join(", ", values);
}
