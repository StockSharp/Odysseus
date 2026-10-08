namespace StockSharp.Odysseus.Compiler;

using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Rules a strategy has to obey that the reference set cannot enforce.
/// </summary>
/// <remarks>
/// Not everything worth forbidding can be kept out by choosing which assemblies to reference. The
/// clock is the clearest case: <c>DateTime.Now</c> lives beside every type a strategy legitimately
/// needs, so no reference set can exclude one without excluding the other.
///
/// None of this is protection against a hostile author — the code belongs to whoever runs it. It is
/// protection of the result. A strategy that reads the wall clock, or draws a number nobody seeded,
/// returns a different answer tomorrow on the same data, and every metric computed from it becomes a
/// number that cannot be checked. Each rule carries a stable identifier, so a refusal names the rule
/// and not merely the member that tripped over it.
/// </remarks>
public static class StrategyRules
{
	private const string Clock = "reads the machine clock, so the same data would produce a different result tomorrow";
	private const string Unseeded = "produces a value nobody seeded, so a re-run cannot reproduce it";

	private static readonly IReadOnlyDictionary<string, (string Rule, string Why)> _forbiddenMembers =
		new Dictionary<string, (string, string)>(StringComparer.Ordinal)
		{
			["System.DateTime.Now"] = ("ODSTR012", Clock),
			["System.DateTime.UtcNow"] = ("ODSTR012", Clock),
			["System.DateTime.Today"] = ("ODSTR012", Clock),
			["System.DateTimeOffset.Now"] = ("ODSTR012", Clock),
			["System.DateTimeOffset.UtcNow"] = ("ODSTR012", Clock),
			["System.Guid.NewGuid"] = ("ODSTR013", Unseeded),
			["System.Guid.CreateVersion7"] = ("ODSTR013", Unseeded),

			// A string's hash is seeded afresh in every process, so anything decided by one is decided
			// differently on the next run.
			["System.String.GetHashCode"] = ("ODSTR013", Unseeded),

			["Ecng.Common.TimeHelper.Now"] = ("ODSTR012", Clock),
			["Ecng.Common.TimeHelper.NowWithOffset"] = ("ODSTR012", Clock),
			["Ecng.Common.TimeHelper.UtcNow"] = ("ODSTR012", Clock),
			["Ecng.Common.TimeHelper.Today"] = ("ODSTR012", Clock),
		};

	// Whole areas that put a run at the mercy of something outside the data. They cannot be excluded by
	// choosing references: their types sit in the same assemblies as the ones a strategy legitimately
	// needs, so the boundary has to be drawn here.
	private static readonly IReadOnlyDictionary<string, (string Rule, string Why)> _forbiddenNamespaces =
		new Dictionary<string, (string, string)>(StringComparer.Ordinal)
		{
			["System.IO"] = ("ODSTR012", "reads or writes the machine, so the same data would not produce the same result elsewhere"),
			["System.Net"] = ("ODSTR012", "depends on something outside the data, which no re-run can reproduce"),
			["System.Diagnostics"] = ("ODSTR012", "observes the process rather than the market"),
			["System.Threading"] = ("ODSTR012", "makes the order of work, and therefore the result, depend on timing"),
			["System.Reflection"] = ("ODSTR012", "reaches past what a strategy is given"),
			["System.Runtime.InteropServices"] = ("ODSTR012", "leaves the managed world, where nothing can be checked"),
			["Ecng.IO"] = ("ODSTR012", "reads or writes the machine, so the same data would not produce the same result elsewhere"),
			["Ecng.Net"] = ("ODSTR012", "depends on something outside the data, which no re-run can reproduce"),
		};

	private static readonly IReadOnlyDictionary<string, (string Rule, string Why)> _forbiddenTypes =
		new Dictionary<string, (string, string)>(StringComparer.Ordinal)
		{
			["System.Random"] = ("ODSTR013", "draws numbers nobody seeded, so a re-run cannot reproduce the result"),
			["System.Environment"] = ("ODSTR012", "exposes the machine the run happens to be on"),
			["Ecng.Common.RandomGen"] = ("ODSTR013", "draws numbers nobody seeded, so a re-run cannot reproduce the result"),
		};

	/// <summary>
	/// Checks compiled source against the rules.
	/// </summary>
	/// <param name="compilation">Compilation the source belongs to.</param>
	/// <param name="tree">Source to check.</param>
	/// <returns>Everything that breaks a rule, one message per place and rule.</returns>
	/// <remarks>
	/// Judged by what each name resolves to rather than by how it is written, so a member reached through
	/// a using static directive, an alias or a fully qualified name is the same member.
	/// </remarks>
	public static IReadOnlyList<CompilerMessage> Inspect(Compilation compilation, SyntaxTree tree)
	{
		ArgumentNullException.ThrowIfNull(compilation);
		ArgumentNullException.ThrowIfNull(tree);

		var model = compilation.GetSemanticModel(tree);
		var messages = new List<CompilerMessage>();
		var seen = new HashSet<(int Line, string Rule)>();

		foreach (var node in tree.GetRoot().DescendantNodes())
		{
			ISymbol symbol = node switch
			{
				SimpleNameSyntax name when !IsInsideUsing(name) => model.GetSymbolInfo(name).Symbol,
				BaseObjectCreationExpressionSyntax creation => model.GetTypeInfo(creation).Type,
				_ => null,
			};

			// A name the reference set does not carry never resolved, and the compiler already refuses it by
			// name; a rule speaking here would name something that is not there to be used.
			if (symbol is null or IErrorTypeSymbol || !Breaks(symbol, out var rule, out var what))
				continue;

			var message = Describe(node, rule.Rule, $"'{what}' {rule.Why}.");

			if (seen.Add((message.Line, message.Id)))
				messages.Add(message);
		}

		return messages;
	}

	private static bool Breaks(ISymbol symbol, out (string Rule, string Why) rule, out string what)
	{
		var type = symbol as ITypeSymbol ?? symbol.ContainingType;

		if (symbol is not ITypeSymbol && type is not null)
		{
			what = $"{FullName(type)}.{symbol.Name}";

			if (_forbiddenMembers.TryGetValue(what, out rule))
				return true;
		}

		if (type is not null)
		{
			what = FullName(type);

			if (_forbiddenTypes.TryGetValue(what, out rule) || Forbidden(type.ContainingNamespace, out rule))
				return true;
		}

		what = null;
		rule = default;
		return false;
	}

	private static bool Forbidden(INamespaceSymbol ns, out (string Rule, string Why) rule)
	{
		var name = ns?.ToDisplayString();

		while (!string.IsNullOrEmpty(name))
		{
			if (_forbiddenNamespaces.TryGetValue(name, out rule))
				return true;

			var separator = name.LastIndexOf('.');

			name = separator < 0 ? null : name[..separator];
		}

		rule = default;
		return false;
	}

	// The name of the type itself, never of a constructed generic or an array of it, so a rule written once
	// covers every shape the type is used in.
	private static string FullName(ITypeSymbol type)
	{
		var definition = type.OriginalDefinition;
		var ns = definition.ContainingNamespace;

		return ns is null || ns.IsGlobalNamespace ? definition.MetadataName : $"{ns.ToDisplayString()}.{definition.MetadataName}";
	}

	// The names in a using directive only say where to look; what is reached is judged where it is used.
	private static bool IsInsideUsing(SyntaxNode node)
		=> node.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null;

	private static CompilerMessage Describe(SyntaxNode node, string rule, string message)
	{
		var position = node.GetLocation().GetLineSpan().StartLinePosition;

		return new(
			rule,
			DiagnosticSeverity.Error.ToString(),
			message + " Use what the context provides instead: it is the same on every run.",
			position.Line + 1,
			position.Character + 1);
	}
}
