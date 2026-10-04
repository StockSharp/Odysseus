namespace Odysseus.Spec.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Domain;
using Odysseus.Spec;
using Odysseus.TestKit;

/// <summary>
/// Holding the published schema against the code it claims to describe.
/// </summary>
/// <remarks>
/// This suite exists because the schemas were once held only against each other. They agreed
/// beautifully and described a different product: a specification document the server would have
/// refused, outputs no tool emitted, and three tools that did not exist. Nothing failed, because
/// nothing compared a schema with a payload.
///
/// So there is one schema for an input, and it is validated against specifications the server really
/// accepts and really refuses. Outputs are not specified here at all: an MCP tool describes itself,
/// and a static copy of an output shape can only drift from it.
/// </remarks>
[TestClass]
public class SchemaContractTests : OdysseusTestBase
{
	private static string SchemaDirectory => Path.Combine(RepositoryRoot, "schemas");

	private static string SampleDirectory => Path.Combine(RepositoryRoot, "samples");

	// Built once however many methods run at once: the library registers a schema by its identifier, and
	// registering the same one twice throws.
	private static JsonSchema Specification => _specification.Value;

	private static readonly Lazy<JsonSchema> _specification = new(() => JsonSchema.FromText(
		File.ReadAllText(Path.Combine(SchemaDirectory, "strategy-spec.schema.json"))));

	private static readonly EvaluationOptions _strict = new() { OutputFormat = OutputFormat.List };

	/// <summary>
	/// The specification the repository ships as its worked example is one the server accepts and one
	/// the schema accepts. If those two ever disagree, the example teaches something that will be
	/// refused.
	/// </summary>
	[TestMethod]
	public void TheSampleIsAcceptedByBothTheServerAndTheSchema()
	{
		var text = File.ReadAllText(Path.Combine(SampleDirectory, "hypothesis.json"));

		var spec = SpecJson.Read(text);

		IsNotNull(spec, "the sample is not a specification the server can read.");

		var checkResult = SpecValidator.Validate(spec);

		IsTrue(checkResult.IsValid,
			"the sample fails the server's own checks: " +
			string.Join("; ", checkResult.Problems.Select(p => p.Message)));

		AssertValid(text, "the sample");
	}

	/// <summary>
	/// Everything the server writes back is something the schema accepts. A round trip is where a
	/// mismatch shows itself: a field the serializer emits and the schema forbids is invisible until
	/// somebody validates what came out.
	/// </summary>
	[TestMethod]
	public void WhatTheServerWritesBackValidates()
	{
		var spec = SpecJson.Read(File.ReadAllText(Path.Combine(SampleDirectory, "hypothesis.json")));

		AssertValid(SpecJson.Write(spec), "what the server wrote back");
	}

	/// <summary>Every shape of expression the vocabulary has is one the schema admits.</summary>
	/// <remarks>
	/// Written as one specification carrying all of them rather than as a list of fragments, because
	/// that is how they arrive: nested inside entries and exits, where a definition that is right on
	/// its own can still be unreachable.
	/// </remarks>
	[TestMethod]
	public void EveryExpressionKindIsAdmitted()
	{
		AssertValid(EveryKind, "a specification using every expression kind");

		var spec = SpecJson.Read(EveryKind);

		IsNotNull(spec, "the server cannot read a specification using its own whole vocabulary.");
	}

	/// <summary>
	/// A specification the server refuses is one the schema refuses. The two do not have to give the
	/// same reason, but an agent must not be able to satisfy the published contract and still be
	/// turned away.
	/// </summary>
	[TestMethod]
	public void WhatTheServerRefusesTheSchemaRefusesToo()
	{
		(string What, string Json)[] refused =
		[
			("an unknown expression kind", EveryKind.Replace("\"kind\": \"Abs\"", "\"kind\": \"Sqrt\"", StringComparison.Ordinal)),
			("a misspelled operator", EveryKind.Replace("\"GreaterThan\"", "\"IsBiggerThan\"", StringComparison.Ordinal)),
			("a candle field that does not exist", EveryKind.Replace("\"field\": \"Close\"", "\"field\": \"Midpoint\"", StringComparison.Ordinal)),
			("an exit kind that does not exist", EveryKind.Replace("\"kind\": \"SessionEnd\"", "\"kind\": \"TrailingStop\"", StringComparison.Ordinal)),
			("a property nobody declared", EveryKind.Replace("\"warmupBars\": 61,", "\"warmupBars\": 61,\n  \"leverage\": 4,", StringComparison.Ordinal)),
		];

		foreach (var (what, json) in refused)
		{
			IsFalse(Evaluate(json).IsValid,
				$"the schema accepts {what}, which the server would refuse.");
		}
	}

	/// <summary>
	/// The words in the schema are the words in the code. An enum that gained a value and a schema that
	/// did not is how a contract starts describing a product that has moved on.
	/// </summary>
	[TestMethod]
	public void TheVocabulariesAreTheOnesTheCodeDeclares()
	{
		Compare<CandleFields>("/$defs/candleField/enum");
		Compare<TradeDirections>("/$defs/tradeDirection/enum");
		Compare<ComparisonOperators>("/$defs/compareNode/properties/operator/enum");
		Compare<CrossDirections>("/$defs/crossNode/properties/direction/enum");
		Compare<PositionStates>("/$defs/positionNode/properties/state/enum");
		Compare<ArithmeticOperators>("/$defs/arithmeticNode/properties/kind/enum");
		Compare<ExitKinds>("/$defs/exitRule/properties/kind/enum");
		Compare<ParameterTypes>("/$defs/parameter/properties/type/enum");
	}

	/// <summary>
	/// A schema has to load on its own. A reference to another document's identifier makes it unusable
	/// to anyone who does not pre-register the whole family first.
	/// </summary>
	[TestMethod]
	public void EverySchemaIsSelfContained()
	{
		foreach (var file in Directory.EnumerateFiles(SchemaDirectory, "*.schema.json"))
		{
			var root = JsonNode.Parse(File.ReadAllText(file));

			foreach (var (path, value) in Walk(root))
			{
				if (!path.EndsWith("/$ref", StringComparison.Ordinal))
					continue;

				IsTrue(value.GetValue<string>().StartsWith("#/", StringComparison.Ordinal),
					$"{Path.GetFileName(file)} at {path} points outside itself.");
			}
		}
	}

	/// <summary>
	/// The prose stays ASCII. These files are read by machines through parsers of every quality, and a
	/// typographic dash in a description is not worth finding out which of them mangles it.
	/// </summary>
	[TestMethod]
	public void ProseIsAscii()
	{
		foreach (var file in Directory.EnumerateFiles(SchemaDirectory, "*.schema.json"))
		{
			var text = File.ReadAllText(file);

			var offending = text.Where(c => c > '').Distinct().ToArray();

			AreEqual(0, offending.Length,
				$"{Path.GetFileName(file)} carries {string.Join(", ", offending.Select(c => $"U+{(int)c:X4}"))}.");
		}
	}

	/// <summary>
	/// Nothing published here describes an output. The moment one does, it is a second source of truth
	/// beside the tool that produces it, and the two drift.
	/// </summary>
	[TestMethod]
	public void OnlyInputsAreSpecified()
	{
		var published = Directory
			.EnumerateFiles(SchemaDirectory, "*.schema.json")
			.Select(Path.GetFileName)
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();

		CollectionAssert.AreEqual(
			new[] { "error.schema.json", "strategy-spec.schema.json" },
			published,
			"a schema was published for something other than the specification an agent writes and the " +
			"shape every failure arrives in. Outputs are described by the tools themselves.");
	}

	private static EvaluationResults Evaluate(string json)
		=> Specification.Evaluate(JsonDocument.Parse(json).RootElement, _strict);

	private static void AssertValid(string json, string what)
	{
		var result = Evaluate(json);

		if (result.IsValid)
			return;

		var said = result.Details
			.Where(d => d.Errors is { Count: > 0 })
			.SelectMany(d => d.Errors.Select(e => $"{d.InstanceLocation}: {e.Value}"))
			.Distinct()
			.Take(8);

		IsTrue(false, $"the schema refuses {what}: {string.Join("; ", said)}");
	}

	// Each vocabulary sits at one place in the schema, so it is read by pointer rather than searched for:
	// a search that found nothing would pass for a schema that declared nothing.
	private static void Compare<T>(string pointer)
		where T : struct, Enum
	{
		var declared = Enum.GetNames<T>().OrderBy(n => n, StringComparer.Ordinal).ToArray();

		var node = Walk(JsonNode.Parse(File.ReadAllText(
				Path.Combine(SchemaDirectory, "strategy-spec.schema.json"))))
			.Where(p => p.Path == pointer)
			.Select(p => p.Value)
			.FirstOrDefault();

		IsNotNull(node, $"the schema declares no vocabulary at {pointer} for {typeof(T).Name}.");

		var found = node.AsArray()
			.Select(v => v.GetValue<string>())
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToArray();

		CollectionAssert.AreEqual(declared, found,
			$"{typeof(T).Name} and the schema at {pointer} have drifted apart.");
	}

	private static IEnumerable<(string Path, JsonNode Value)> Walk(JsonNode node, string path = "")
	{
		switch (node)
		{
			case JsonObject o:
				foreach (var (key, value) in o)
				{
					if (value is null)
						continue;

					yield return ($"{path}/{key}", value);

					foreach (var nested in Walk(value, $"{path}/{key}"))
						yield return nested;
				}

				break;

			case JsonArray a:
				for (var i = 0; i < a.Count; i++)
				{
					if (a[i] is not { } item)
						continue;

					foreach (var nested in Walk(item, $"{path}/{i}"))
						yield return nested;
				}

				break;
		}
	}

	// Every kind the converter can write, in one document, nested where they really appear.
	private const string EveryKind = """
		{
		  "name": "Every kind",
		  "thesis": "A specification exercising the whole expression vocabulary at once.",
		  "allowLong": true,
		  "allowShort": true,
		  "timeFrame": "00:05:00",
		  "warmupBars": 61,
		  "entries": [
		    {
		      "id": "e1",
		      "direction": "Long",
		      "condition": {
		        "kind": "All",
		        "conditions": [
		          { "kind": "Any", "conditions": [
		            { "kind": "Position", "state": "Flat" },
		            { "kind": "Not", "condition": { "kind": "Position", "state": "Short" } }
		          ] },
		          { "kind": "Session", "notBefore": "09:45", "notAfter": "15:30" },
		          { "kind": "Cross",
		            "left": { "kind": "Field", "field": "Close" },
		            "direction": "Above",
		            "right": { "kind": "Indicator", "name": "sma",
		                       "length": { "kind": "Parameter", "name": "Window" }, "source": "Close", "offset": 1 } },
		          { "kind": "Compare",
		            "left": { "kind": "Abs", "value": {
		              "kind": "Subtract", "operands": [
		                { "kind": "Field", "field": "Close" },
		                { "kind": "Field", "field": "Open", "offset": 1 }
		              ] } },
		            "operator": "GreaterThan",
		            "right": { "kind": "Multiply", "operands": [
		              { "kind": "Constant", "value": 0.5 },
		              { "kind": "Indicator", "name": "atr", "length": { "kind": "Constant", "value": 14 } }
		            ] } },
		          { "kind": "Compare",
		            "left": { "kind": "Min", "operands": [
		              { "kind": "Field", "field": "High" },
		              { "kind": "Add", "operands": [
		                { "kind": "Field", "field": "Low" },
		                { "kind": "Constant", "value": 1 } ] } ] },
		            "operator": "LessOrEqual",
		            "right": { "kind": "Max", "operands": [
		              { "kind": "Divide", "operands": [
		                { "kind": "Field", "field": "Volume" },
		                { "kind": "Constant", "value": 2 } ] },
		              { "kind": "Indicator", "name": "volumeSma",
		                "length": { "kind": "Constant", "value": 20 } } ] } }
		        ]
		      }
		    }
		  ],
		  "exits": [
		    { "id": "x1", "kind": "Condition", "direction": "Long",
		      "condition": { "kind": "Compare",
		        "left": { "kind": "Field", "field": "Close" },
		        "operator": "LessThan",
		        "right": { "kind": "Indicator", "name": "lowest",
		                   "length": { "kind": "Constant", "value": 10 }, "source": "Low", "offset": 1 } } },
		    { "id": "x2", "kind": "AtrStop", "direction": "Long",
		      "length": { "kind": "Constant", "value": 14 },
		      "multiplier": { "kind": "Constant", "value": 2 } },
		    { "id": "x3", "kind": "AtrTarget", "direction": "Long",
		      "length": { "kind": "Constant", "value": 14 },
		      "multiplier": { "kind": "Constant", "value": 3 } },
		    { "id": "x4", "kind": "TimeExit", "direction": "Long",
		      "length": { "kind": "Constant", "value": 12 } },
		    { "id": "x5", "kind": "SessionEnd", "direction": "Long" }
		  ],
		  "parameters": [
		    { "name": "Window", "type": "Integer", "default": 20, "minimum": 10, "maximum": 60, "step": 5 }
		  ],
		  "risk": { "maxPositionPercent": 0.1, "maxDailyLossPercent": 0.02 }
		}
		""";
}
