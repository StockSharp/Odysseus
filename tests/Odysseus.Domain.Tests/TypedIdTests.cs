namespace Odysseus.Domain.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Domain;
using Odysseus.TestKit;

/// <summary>
/// Typed identifiers. A bare string or a bare GUID passed between layers eventually gets handed to
/// the wrong parameter, and nothing catches it; these types make that a compile error. The format is
/// also the one the contracts declare, so an identifier the domain can produce is always an identifier
/// the schemas accept.
/// </summary>
[TestClass]
public class TypedIdTests : OdysseusTestBase
{
	// The identifier definition shared by every schema in the contract family.
	private static readonly Regex _contractPattern = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);

	/// <summary>A freshly issued identifier of every kind must satisfy the published contract.</summary>
	[TestMethod]
	public void IssuedIdentifiersMatchTheContract()
	{
		foreach (var (kind, value) in NewOfEveryKind())
		{
			IsTrue(_contractPattern.IsMatch(value),
				$"{kind} issued '{value}', which the contract pattern rejects.");

			IsTrue(value.Length <= 64, $"{kind} issued an identifier longer than the contract allows.");
		}
	}

	/// <summary>Each kind is recognisable on sight, which is what makes a mixed-up identifier catchable.</summary>
	[TestMethod]
	public void EachKindCarriesItsOwnPrefix()
	{
		var prefixes = NewOfEveryKind()
			.Select(pair => pair.Value[..pair.Value.IndexOf('_')])
			.ToArray();

		AreEqual(prefixes.Length, prefixes.Distinct().Count(), "two kinds share a prefix.");
	}

	/// <summary>Formatting and parsing must be exact inverses, or persistence loses identity.</summary>
	[TestMethod]
	public void ParsingRoundTrips()
	{
		var id = ProjectId.New();

		AreEqual(id, ProjectId.Parse(id.ToString()));
		AreEqual(id.Value, ProjectId.Parse(id.Value).Value);
	}

	/// <summary>An identifier of one kind must not parse as another.</summary>
	[TestMethod]
	public void KindsDoNotAcceptEachOthersValues()
	{
		var project = ProjectId.New();

		Throws<FormatException>(() => CandidateId.Parse(project.Value));
		IsFalse(CandidateId.TryParse(project.Value, out _));
	}

	/// <summary>Malformed text is refused rather than carried around as a broken identifier.</summary>
	[TestMethod]
	public void MalformedValuesAreRefused()
	{
		string[] malformed =
		[
			null,
			"",
			"   ",
			"prj",
			"prj_",
			"_prj_abc",
			"prj_abc def",
			"prj_abc/def",
			"prj_" + new string('a', 64),
		];

		foreach (var value in malformed)
		{
			IsFalse(ProjectId.TryParse(value, out _), $"'{value ?? "<null>"}' must not parse.");
			Throws<FormatException>(() => ProjectId.Parse(value));
		}
	}

	/// <summary>Two identifiers issued separately must differ.</summary>
	[TestMethod]
	public void IssuedIdentifiersAreUnique()
	{
		var issued = Enumerable.Range(0, 1000).Select(_ => ProjectId.New()).ToArray();

		AreEqual(issued.Length, issued.Distinct().Count(), "issued identifiers collided.");
	}

	/// <summary>Identifiers are used as dictionary keys throughout, so equality must behave.</summary>
	[TestMethod]
	public void EqualityWorksAsAKey()
	{
		var id = RunId.New();
		var same = RunId.Parse(id.Value);

		AreEqual(id, same);
		AreEqual(id.GetHashCode(), same.GetHashCode());

		var map = new Dictionary<RunId, int> { [id] = 1 };

		IsTrue(map.ContainsKey(same), "an equal identifier must find the same entry.");
	}

	/// <summary>
	/// A default value is not an identifier. It must be recognisable, so that a field nobody assigned
	/// fails loudly instead of quietly meaning something.
	/// </summary>
	[TestMethod]
	public void DefaultValueIsNotAnIdentifier()
	{
		var empty = default(ProjectId);

		IsTrue(empty.IsEmpty);
		IsFalse(ProjectId.New().IsEmpty);

		Throws<InvalidOperationException>(() => _ = empty.Value);
	}

	private static IEnumerable<(string Kind, string Value)> NewOfEveryKind()
	{
		yield return (nameof(ProjectId), ProjectId.New().Value);
		yield return (nameof(DatasetId), DatasetId.New().Value);
		yield return (nameof(SpecId), SpecId.New().Value);
		yield return (nameof(CandidateId), CandidateId.New().Value);
		yield return (nameof(RunId), RunId.New().Value);
		yield return (nameof(JobId), JobId.New().Value);
		yield return (nameof(DeploymentId), DeploymentId.New().Value);
		yield return (nameof(ArtifactId), ArtifactId.FromContent("content"u8).Value);
	}
}
