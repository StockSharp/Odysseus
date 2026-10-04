namespace Odysseus.Compiler;

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

using Odysseus.Application;
using Odysseus.CodeGen;
using Odysseus.Spec;

/// <summary>
/// Translating a specification and compiling what comes out, as one step.
/// </summary>
/// <remarks>
/// A failure at either end is reported the same way, because from where the caller stands they are the
/// same failure: the specification did not become a strategy. What differs is where to look, and that
/// is what the rule identifier on each problem says.
/// </remarks>
public sealed class StrategyBuilder : IStrategyBuilder, IStrategyInspector
{
	private readonly StrategyCompiler _compiler;

	/// <summary>
	/// Creates the builder.
	/// </summary>
	/// <param name="engineAssemblies">Paths of the assemblies a generated strategy is written against.</param>
	public StrategyBuilder(IEnumerable<string> engineAssemblies)
	{
		_compiler = new(engineAssemblies);
	}

	/// <inheritdoc />
	public IReadOnlyList<BuildProblem> Inspect(string source)
		=> [.. _compiler.Inspect(source).Select(m => new BuildProblem(m.Id, m.Message, m.Line))];

	/// <inheritdoc />
	public BuiltStrategy Build(StrategySpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);

		var translated = StrategyTranslator.Translate(spec);
		var compiled = _compiler.Compile(translated.Source, translated.ClassName);

		if (!compiled.Succeeded)
		{
			throw new StrategyBuildException(
			[
				.. compiled.Messages
					.Where(m => m.Severity != nameof(DiagnosticSeverity.Warning))
					.Select(m => new BuildProblem(m.Id, m.Message, m.Line)),
			]);
		}

		return new(
			translated.ClassName,
			translated.Source,
			translated.SourceHash,
			compiled.Assembly,
			compiled.AssemblyHash,
			StrategyTranslator.Version);
	}
}
