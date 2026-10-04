namespace Odysseus.Persistence.Tests;

using System.Collections.Generic;
using System.Text;
using System.Threading;

using Odysseus.Application;

/// <summary>
/// The content-addressed artifact store. Everything the product claims about a candidate — this source
/// produced this assembly, which produced these metrics — rests on artifacts being exactly what they
/// were when they were written. A store that cannot detect a changed byte turns every hash in the
/// reports into decoration.
/// </summary>
[TestClass]
public class ArtifactStoreTests : OdysseusTestBase
{
	// Every artifact belongs to a project, so the checks below work inside one.
	private static readonly ProjectId _project = ProjectId.New();
	private string _root;
	private FileArtifactStore _store;

	/// <summary>Creates a store over a temporary directory.</summary>
	[TestInitialize]
	public void CreateStore()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new FileArtifactStore(_root);
	}

	/// <summary>Removes the temporary directory.</summary>
	[TestCleanup]
	public void DeleteStore()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// An artifact lives inside the project it belongs to, and nothing lands beside the project folders.
	/// A project is meant to be a thing you can zip and open elsewhere; a shared pool keyed by hash alone
	/// would leave the copy with a database pointing at files that stayed behind.
	/// </summary>
	[TestMethod]
	public async Task AnArtifactLivesInsideItsProject()
	{
		var descriptor = await _store.PutAsync(_project, Encoding.UTF8.GetBytes("class Candidate { }"), CancellationToken);

		var inside = Directory.EnumerateFiles(Path.Combine(_root, _project.Value), "*", SearchOption.AllDirectories).ToArray();

		AreEqual(1, inside.Length, "the artifact was not written inside the project.");
		IsTrue(inside[0].EndsWith(descriptor.Sha256, StringComparison.Ordinal));

		var strays = Directory
			.EnumerateFileSystemEntries(_root)
			.Where(e => Path.GetFileName(e) != _project.Value)
			.ToArray();

		AreEqual(0, strays.Length,
			$"something was written beside the project folders: {string.Join(", ", strays.Select(Path.GetFileName))}");
	}

	/// <summary>
	/// Two projects storing the same bytes each keep their own copy. Sharing them would save a little
	/// disk and cost the property the copy depends on.
	/// </summary>
	[TestMethod]
	public async Task ProjectsDoNotShareArtifacts()
	{
		var other = ProjectId.New();
		var content = Encoding.UTF8.GetBytes("the same bytes in two projects");

		var mine = await _store.PutAsync(_project, content, CancellationToken);
		var theirs = await _store.PutAsync(other, content, CancellationToken);

		AreEqual(mine.Id, theirs.Id, "identical content is still the same artifact.");

		IsTrue(await _store.VerifyAsync(_project, mine.Id, CancellationToken));
		IsTrue(await _store.VerifyAsync(other, theirs.Id, CancellationToken));

		// Deleting one project leaves the other whole.
		Directory.Delete(Path.Combine(_root, other.Value), recursive: true);

		IsTrue(await _store.VerifyAsync(_project, mine.Id, CancellationToken),
			"removing one project took the other's artifact with it.");
	}

	/// <summary>An artifact of one project is not readable through another.</summary>
	[TestMethod]
	public async Task OneProjectCannotReadAnotherArtifact()
	{
		var descriptor = await _store.PutAsync(_project, Encoding.UTF8.GetBytes("private to this project"), CancellationToken);

		await ThrowsAsync<ArtifactNotFoundException>(
			() => _store.ReadAsync(ProjectId.New(), descriptor.Id, CancellationToken).AsTask());
	}

	/// <summary>What went in comes back byte for byte.</summary>
	[TestMethod]
	public async Task StoredContentIsReturnedUnchanged()
	{
		var content = Encoding.UTF8.GetBytes("public sealed class Candidate { }");

		var descriptor = await _store.PutAsync(_project, content, CancellationToken);
		var read = await _store.ReadAsync(_project, descriptor.Id, CancellationToken);

		IsTrue(content.SequenceEqual(read), "the artifact came back different from what was written.");
	}

	/// <summary>
	/// The identifier is a function of the content alone. Two writes of the same bytes are the same
	/// artifact, whenever they happen and whatever they are called.
	/// </summary>
	[TestMethod]
	public async Task IdentityComesFromContentOnly()
	{
		var content = Encoding.UTF8.GetBytes("the same bytes");

		var first = await _store.PutAsync(_project, content, CancellationToken);
		var second = await _store.PutAsync(_project, content, CancellationToken);

		AreEqual(first.Id, second.Id);
		AreEqual(first.Sha256, second.Sha256);
		AreEqual(first.Length, second.Length);
	}

	/// <summary>Different content is a different artifact.</summary>
	[TestMethod]
	public async Task DifferentContentIsADifferentArtifact()
	{
		var first = await _store.PutAsync(_project, Encoding.UTF8.GetBytes("a"), CancellationToken);
		var second = await _store.PutAsync(_project, Encoding.UTF8.GetBytes("b"), CancellationToken);

		AreNotEqual(first.Id, second.Id);
		AreNotEqual(first.Sha256, second.Sha256);
	}

	/// <summary>Writing the same content twice must not store it twice.</summary>
	[TestMethod]
	public async Task IdenticalContentIsStoredOnce()
	{
		var content = Encoding.UTF8.GetBytes("stored once");

		await _store.PutAsync(_project, content, CancellationToken);
		await _store.PutAsync(_project, content, CancellationToken);

		var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToArray();

		AreEqual(1, files.Length, "the same content was written to disk more than once.");
	}

	/// <summary>
	/// This is the test the store exists for: a byte changed behind its back must be detected, not
	/// served.
	/// </summary>
	[TestMethod]
	public async Task TamperingIsDetected()
	{
		var descriptor = await _store.PutAsync(_project, Encoding.UTF8.GetBytes("original content"), CancellationToken);

		IsTrue(await _store.VerifyAsync(_project, descriptor.Id, CancellationToken), "a freshly written artifact must verify.");

		var file = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Single();
		await File.WriteAllTextAsync(file, "tampered content", CancellationToken);

		IsFalse(await _store.VerifyAsync(_project, descriptor.Id, CancellationToken),
			"a changed artifact verified as intact.");

		await ThrowsAsync<ArtifactCorruptedException>(
			async () => await _store.ReadAsync(_project, descriptor.Id, CancellationToken));
	}

	/// <summary>An artifact that was never written is missing, not empty.</summary>
	[TestMethod]
	public async Task MissingArtifactIsReported()
	{
		var absent = ArtifactId.FromContent("never stored"u8);

		IsFalse(await _store.VerifyAsync(_project, absent, CancellationToken));

		await ThrowsAsync<ArtifactNotFoundException>(
			async () => await _store.ReadAsync(_project, absent, CancellationToken));
	}

	/// <summary>The store keeps its files under its own root and nowhere else.</summary>
	[TestMethod]
	public async Task NothingIsWrittenOutsideTheRoot()
	{
		await _store.PutAsync(_project, Encoding.UTF8.GetBytes("inside"), CancellationToken);

		var root = Path.GetFullPath(_root);

		foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
		{
			IsTrue(Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase),
				$"{file} lies outside the store root.");
		}
	}

	/// <summary>
	/// A write interrupted halfway must leave nothing readable behind, or the next verification finds a
	/// truncated artifact and calls the project corrupt.
	/// </summary>
	[TestMethod]
	public async Task InterruptedWriteLeavesNoArtifact()
	{
		var content = Encoding.UTF8.GetBytes("interrupted");

		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		await ThrowsAsync<OperationCanceledException>(
			async () => await _store.PutAsync(_project, content, cts.Token));

		var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToArray();

		AreEqual(0, files.Length, "a cancelled write left a file behind.");
	}

	/// <summary>Several writers of the same content must not corrupt each other.</summary>
	[TestMethod]
	public async Task ConcurrentWritesOfTheSameContentAgree()
	{
		var content = Encoding.UTF8.GetBytes("written by many at once");

		var descriptors = await Task.WhenAll(Enumerable
			.Range(0, 16)
			.Select(_ => _store.PutAsync(_project, content, CancellationToken).AsTask()));

		AreEqual(1, descriptors.Select(d => d.Id).Distinct().Count(), "writers disagreed about the identity.");
		IsTrue(await _store.VerifyAsync(_project, descriptors[0].Id, CancellationToken));
	}

	/// <summary>The descriptor reports the real size, which the reports quote.</summary>
	[TestMethod]
	public async Task DescriptorReportsTheRealLength()
	{
		var content = Encoding.UTF8.GetBytes(new string('x', 4096));

		var descriptor = await _store.PutAsync(_project, content, CancellationToken);

		AreEqual(content.Length, descriptor.Length);
	}

	/// <summary>An empty artifact is still an artifact, and must round-trip like any other.</summary>
	[TestMethod]
	public async Task EmptyContentRoundTrips()
	{
		var descriptor = await _store.PutAsync(_project, Array.Empty<byte>(), CancellationToken);

		AreEqual(0, descriptor.Length);
		AreEqual(0, (await _store.ReadAsync(_project, descriptor.Id, CancellationToken)).Length);
		IsTrue(await _store.VerifyAsync(_project, descriptor.Id, CancellationToken));
	}

	/// <summary>The store survives a restart: identity does not live in memory.</summary>
	[TestMethod]
	public async Task ArtifactsSurviveARestart()
	{
		var descriptor = await _store.PutAsync(_project, Encoding.UTF8.GetBytes("persisted"), CancellationToken);

		var reopened = new FileArtifactStore(_root);

		IsTrue(await reopened.VerifyAsync(_project, descriptor.Id, CancellationToken));
		AreEqual("persisted", Encoding.UTF8.GetString(await reopened.ReadAsync(_project, descriptor.Id, CancellationToken)));
	}
}
