namespace StockSharp.Odysseus.Compiler;

using System.IO;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// One thing the compiler had to say about the source.
/// </summary>
/// <param name="Id">Identifier of the rule, such as <c>CS0103</c>.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Line">Line it is on, counting from one.</param>
/// <param name="Column">Column it starts at, counting from one.</param>
public sealed record CompilerMessage(string Id, string Severity, string Message, int Line, int Column);

/// <summary>
/// What compiling produced.
/// </summary>
/// <param name="Succeeded">Whether an assembly came out.</param>
/// <param name="Messages">Everything the compiler had to say, errors first.</param>
/// <param name="Assembly">The compiled assembly, when compilation succeeded.</param>
/// <param name="AssemblyHash">Hash of the assembly, which the candidate is identified by.</param>
public sealed record CompilationOutcome(
	bool Succeeded,
	IReadOnlyList<CompilerMessage> Messages,
	byte[] Assembly,
	string AssemblyHash);

/// <summary>
/// Compiles a generated strategy.
/// </summary>
/// <remarks>
/// The reference set is decided here and never by the code being compiled. That is not a sandbox — the
/// code belongs to the person running it — but it is what keeps a strategy reproducible: a strategy
/// that can reach the clock, the file system or the network is one that can return a different answer
/// tomorrow, and a research tool whose answers move measures nothing.
///
/// Compilation is deterministic, so the same source always produces the same assembly and a candidate
/// can be identified by its hash rather than by when it happened to be built.
/// </remarks>
public sealed class StrategyCompiler
{
	// What a strategy is allowed to see of the framework, and no more. These are reference assemblies
	// carried by the product rather than whatever happens to sit in the runtime folder: the ones there
	// are facades that forward into a single implementation assembly, so referencing them would let the
	// whole framework in through one door and make this list decoration.
	private static readonly string[] _allowed =
	[
		"System.Runtime",
		"System.Collections",
		"System.Linq",
	];

	private readonly IReadOnlyList<MetadataReference> _references;

	/// <summary>
	/// Creates the compiler.
	/// </summary>
	/// <param name="engineAssemblies">
	/// Paths of the assemblies a generated strategy is written against. The compiler does not name the
	/// trading engine itself: whoever knows which engine is in use supplies it, so this project stays free
	/// of it and the reference set remains something decided in one place rather than discovered.
	/// </param>
	public StrategyCompiler(IEnumerable<string> engineAssemblies)
	{
		ArgumentNullException.ThrowIfNull(engineAssemblies);

		var references = Basic.Reference.Assemblies.Net100.References.All
			.Where(r => _allowed.Contains(Path.GetFileNameWithoutExtension(r.FilePath ?? r.Display), StringComparer.Ordinal))
			.Cast<MetadataReference>()
			.ToList();

		if (references.Count != _allowed.Length)
		{
			throw new InvalidOperationException(
				$"Expected {_allowed.Length} reference assemblies but resolved {references.Count}. " +
				"A strategy compiled against the wrong set would either fail to build or see more than it should.");
		}

		var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var path in engineAssemblies)
		{
			if (!File.Exists(path) || !added.Add(Path.GetFileName(path)))
				continue;

			references.Add(MetadataReference.CreateFromFile(path));
		}

		_references = references;
	}

	/// <summary>
	/// Compiles source into an assembly.
	/// </summary>
	/// <param name="source">Source to compile.</param>
	/// <param name="assemblyName">Name for the assembly, which must not vary between builds of the same source.</param>
	/// <returns>What came out.</returns>
	public CompilationOutcome Compile(string source, string assemblyName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

		var (compilation, tree) = Prepare(source, assemblyName);

		// The rules are checked before emitting: a strategy that breaks one is not a candidate, and
		// producing an assembly for it would only invite someone to run it.
		var broken = StrategyRules.Inspect(compilation, tree);

		if (broken.Count > 0)
			return new(false, broken, null, null);

		using var stream = new MemoryStream();

		var result = compilation.Emit(stream);

		var messages = result.Diagnostics
			.Where(d => d.Severity >= DiagnosticSeverity.Warning)
			.OrderByDescending(d => d.Severity)
			.ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
			.Select(Describe)
			.ToArray();

		if (!result.Success)
			return new(false, messages, null, null);

		var assembly = stream.ToArray();

		return new(true, messages, assembly, Convert.ToHexStringLower(SHA256.HashData(assembly)));
	}

	/// <summary>
	/// Checks source against the rules a reproducible strategy keeps, without building it.
	/// </summary>
	/// <param name="source">Source to check.</param>
	/// <returns>Everything that breaks a rule.</returns>
	public IReadOnlyList<CompilerMessage> Inspect(string source)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(source);

		var (compilation, tree) = Prepare(source, "inspected");

		return StrategyRules.Inspect(compilation, tree);
	}

	private static CompilerMessage Describe(Diagnostic diagnostic)
	{
		var position = diagnostic.Location.GetLineSpan().StartLinePosition;

		return new(
			diagnostic.Id,
			diagnostic.Severity.ToString(),
			diagnostic.GetMessage(),
			position.Line + 1,
			position.Character + 1);
	}

	private (CSharpCompilation Compilation, SyntaxTree Tree) Prepare(string source, string assemblyName)
	{
		var tree = CSharpSyntaxTree.ParseText(
			source,
			new CSharpParseOptions(LanguageVersion.Latest),
			encoding: Encoding.UTF8);

		var compilation = CSharpCompilation.Create(
			assemblyName,
			[tree],
			_references,
			new CSharpCompilationOptions(
				OutputKind.DynamicallyLinkedLibrary,
				optimizationLevel: OptimizationLevel.Release,
				// Without this the assembly carries a fresh module version every build, and the same
				// source would produce a different hash each time it was compiled.
				deterministic: true,
				allowUnsafe: false));

		return (compilation, tree);
	}
}
