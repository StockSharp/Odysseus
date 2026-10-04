namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Linq;

using Odysseus.Spec;

/// <summary>
/// What building a specification produced.
/// </summary>
/// <param name="ClassName">Name of the generated strategy class.</param>
/// <param name="Source">The generated C#.</param>
/// <param name="SourceHash">Identity of the strategy: the hash of its source without the wording.</param>
/// <param name="Assembly">The compiled assembly.</param>
/// <param name="AssemblyHash">Hash of the assembly.</param>
/// <param name="TranslatorVersion">Version of the translator that produced the source.</param>
public sealed record BuiltStrategy(
	string ClassName,
	string Source,
	string SourceHash,
	byte[] Assembly,
	string AssemblyHash,
	string TranslatorVersion);

/// <summary>
/// Why a specification could not be built.
/// </summary>
/// <param name="Rule">Rule the code broke, or the compiler's own identifier.</param>
/// <param name="Message">What is wrong, in the words a reader needs.</param>
/// <param name="Line">Line of the generated source, when the problem has one.</param>
public sealed record BuildProblem(string Rule, string Message, int Line);

/// <summary>
/// Turns a checked specification into a compiled strategy.
/// </summary>
/// <remarks>
/// Translation and compilation are one step from the outside because neither is useful alone: source
/// that does not compile is not a candidate, and a candidate is identified by the hash of the source
/// that produced its assembly. Keeping them behind one port also keeps the compiler — and the whole of
/// Roslyn with it — out of the use cases, which never need to know that C# is involved at all.
/// </remarks>
public interface IStrategyBuilder
{
	/// <summary>
	/// Builds a specification.
	/// </summary>
	/// <param name="spec">The specification to build.</param>
	/// <returns>The built strategy.</returns>
	/// <exception cref="StrategyBuildException">The source was refused or would not compile.</exception>
	BuiltStrategy Build(StrategySpec spec);
}

/// <summary>
/// Raised when a specification could not be turned into a running strategy.
/// </summary>
public sealed class StrategyBuildException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="problems">Everything found wrong, so one call reports all of it.</param>
	public StrategyBuildException(IReadOnlyList<BuildProblem> problems)
		: base(Describe(problems))
	{
		Problems = problems;
	}

	/// <summary>Everything found wrong.</summary>
	public IReadOnlyList<BuildProblem> Problems { get; }

	/// <summary>Whether the source broke a rule of this server rather than a rule of the language.</summary>
	/// <remarks>
	/// Our analyzers are identified by a prefix of their own, so the two kinds of problem can be told
	/// apart without the compiler having to be asked twice. They are read differently: one is code that
	/// cannot run, the other is code that must not.
	/// </remarks>
	public bool IsAnalyzerViolation
		=> Problems.Count > 0 && Problems.All(p => p.Rule.StartsWith("OD", StringComparison.Ordinal));

	/// <summary>
	/// Lists every problem with the line it was found on.
	/// </summary>
	/// <returns>The list, as one sentence per problem.</returns>
	public string Enumerate()
		=> string.Join("; ", Problems.Select(p => p.Line > 0
			? $"{p.Rule} at line {p.Line}: {p.Message}"
			: $"{p.Rule}: {p.Message}"));

	private static string Describe(IReadOnlyList<BuildProblem> problems)
		=> "The strategy could not be built: " +
			string.Join("; ", problems.Select(p => p.Line > 0
				? $"{p.Rule} at line {p.Line}: {p.Message}"
				: $"{p.Rule}: {p.Message}"));
}
