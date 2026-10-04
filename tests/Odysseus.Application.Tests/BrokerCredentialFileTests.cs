namespace Odysseus.Application.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.TestKit;

/// <summary>
/// Reading the file the broker credentials live in.
/// </summary>
/// <remarks>
/// There used to be four readers of this file and two of them disagreed: the server accepted an equals
/// sign and the command line did not, so a person whose file used one was told there was no broker
/// rather than that the file could not be read. One reader now, and the looser form wins, because a
/// file a person writes by hand is allowed both separators and spaces around them.
/// </remarks>
[TestClass]
public class BrokerCredentialFileTests : OdysseusTestBase
{
	/// <summary>The colon form, which is what the documentation has always shown.</summary>
	[TestMethod]
	public void AKeyAndSecretAreRead()
	{
		var read = BrokerCredentialFile.Parse("key: ABC123\nsecret: shhh\n");

		AreEqual(BrokerCredentialKinds.KeySecret, read.Kind);
		AreEqual("ABC123", read.Value("key"));
		AreEqual("shhh", read.Value("secret"));
	}

	/// <summary>
	/// The equals form, which one of the four readers silently reported as no broker at all.
	/// </summary>
	[TestMethod]
	public void TheEqualsFormIsReadEverywhere()
	{
		var read = BrokerCredentialFile.Parse("key = ABC123\nsecret = shhh");

		IsNotNull(read, "a file written with an equals sign was read as no credentials at all.");
		AreEqual("ABC123", read.Value("key"));
	}

	/// <summary>
	/// Which shape the file carries is inferred from the names in it, so a connector taking something
	/// other than a key and a secret needs no new variable and no new reader.
	/// </summary>
	[TestMethod]
	public void TheShapeIsInferredFromTheNames()
	{
		AreEqual(BrokerCredentialKinds.LoginPassword, BrokerCredentialFile.Parse("login: me\npassword: mine").Kind);
		AreEqual(BrokerCredentialKinds.Token, BrokerCredentialFile.Parse("token: t-t-t").Kind);
		AreEqual(BrokerCredentialKinds.Passphrase, BrokerCredentialFile.Parse("passphrase: open sesame").Kind);
	}

	/// <summary>Half a pair is not credentials, and is reported as none rather than as something broken.</summary>
	[TestMethod]
	public void HalfAPairIsNotCredentials()
		=> IsNull(BrokerCredentialFile.Parse("key: ABC123"), "a key with no secret was read as usable credentials.");

	/// <summary>Nothing at all is nothing, rather than an exception on a machine that simply has no keys.</summary>
	[TestMethod]
	public void NothingIsNothing()
	{
		IsNull(BrokerCredentialFile.Parse(string.Empty));
		IsNull(BrokerCredentialFile.Read(null));
		IsNull(BrokerCredentialFile.Read("  "));
	}

	/// <summary>The names are read whatever case the file writes them in.</summary>
	[TestMethod]
	public void TheNamesAreCaseInsensitive()
	{
		var read = BrokerCredentialFile.Parse("KEY: ABC123\nSecret: shhh");

		AreEqual(BrokerCredentialKinds.KeySecret, read.Kind);
		AreEqual("ABC123", read.Value("key"));
	}

	/// <summary>A name the file does not carry reads as empty rather than as a failure.</summary>
	[TestMethod]
	public void AnAbsentNameIsEmpty()
		=> AreEqual(
			string.Empty,
			BrokerCredentialFile.Parse("token: t").Value("secret"),
			"a name the file does not carry came back as something.");
}
