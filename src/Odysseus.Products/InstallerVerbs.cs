namespace StockSharp.Odysseus.Products;

/// <summary>
/// The commands of the installer console this server ever uses.
/// </summary>
/// <remarks>
/// The console offers more of them. <c>repair</c> is a remove followed by an install with one extra
/// flag, <c>license</c> and <c>licenses</c> are about entitlements rather than software, and <c>sign</c>
/// uploads an assembly to StockSharp to be signed. None of the three belong on the tool surface of a
/// research server, so none of them are named here: a verb this enumeration cannot spell is a verb no
/// argument list can carry.
/// </remarks>
public enum InstallerVerbs
{
	/// <summary>Lists what the store offers.</summary>
	Products,

	/// <summary>Lists what is installed on this machine.</summary>
	Installed,

	/// <summary>Installs one product.</summary>
	Install,

	/// <summary>Updates one installed product.</summary>
	Update,

	/// <summary>Removes one installed product.</summary>
	Remove,

	/// <summary>Reads the identifier the store licences products against.</summary>
	HddId,
}
