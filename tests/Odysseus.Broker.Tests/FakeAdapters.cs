namespace StockSharp.Odysseus.Broker.Tests;

using Ecng.Common;

using StockSharp.Messages;

/// <summary>
/// A connector with no way of being told it is on a paper account.
/// </summary>
/// <remarks>
/// Public rather than nested and private because the code under test builds an adapter by reflection,
/// the way it builds one out of a downloaded package, and reflection will not construct a type it
/// cannot see.
/// </remarks>
public sealed class NoDemoAdapter : MessageAdapter
{
	/// <summary>
	/// Creates the adapter.
	/// </summary>
	/// <param name="ids">Generator the adapter numbers its transactions with.</param>
	public NoDemoAdapter(IdGenerator ids)
		: base(ids)
	{
	}
}

/// <summary>
/// A connector that takes the demo flag and does not keep it.
/// </summary>
public sealed class StubbornAdapter : MessageAdapter, IDemoAdapter
{
	/// <summary>
	/// Creates the adapter.
	/// </summary>
	/// <param name="ids">Generator the adapter numbers its transactions with.</param>
	public StubbornAdapter(IdGenerator ids)
		: base(ids)
	{
	}

	/// <inheritdoc />
	public bool IsDemo
	{
		get => false;
		set { }
	}
}

/// <summary>
/// A connector whose demo flag changes a second setting, which is what a real one does.
/// </summary>
public sealed class FeedAdapter : MessageAdapter, IDemoAdapter
{
	private bool _isDemo;

	/// <summary>
	/// Creates the adapter.
	/// </summary>
	/// <param name="ids">Generator the adapter numbers its transactions with.</param>
	public FeedAdapter(IdGenerator ids)
		: base(ids)
	{
	}

	/// <inheritdoc />
	public bool IsDemo
	{
		get => _isDemo;
		set
		{
			_isDemo = value;

			if (value)
				Feed = "single-exchange";
		}
	}

	/// <summary>Which feed the adapter would read, so the flag's side effect is observable.</summary>
	public string Feed { get; set; } = "single-exchange";
}

/// <summary>
/// A connector that goes into demo mode willingly and then comes back out of it when one of its
/// ordinary settings is applied.
/// </summary>
/// <remarks>
/// The shape a real one has: the demo flag does not name a host, it selects one, and a connector that
/// lets the caller name the venue directly rebuilds its endpoint from that name and leaves the demo
/// host behind. Nothing about it looks like a lever on the paper guarantee - <c>Venue</c> is refused by
/// no rule, and the flag was true when it was checked - which is exactly why the flag has to be read
/// again after the settings rather than only before them.
/// </remarks>
public sealed class VenueAdapter : MessageAdapter, IDemoAdapter
{
	private bool _isDemo;
	private string _venue = "demo";

	/// <summary>
	/// Creates the adapter.
	/// </summary>
	/// <param name="ids">Generator the adapter numbers its transactions with.</param>
	public VenueAdapter(IdGenerator ids)
		: base(ids)
	{
	}

	/// <inheritdoc />
	public bool IsDemo
	{
		get => _isDemo;
		set
		{
			_isDemo = value;

			if (value)
				Feed = "single-exchange";
		}
	}

	/// <summary>
	/// Which venue the adapter points at. Naming one takes the adapter off the demo host, so this is the
	/// setting the read-back exists to catch.
	/// </summary>
	public string Venue
	{
		get => _venue;
		set
		{
			_venue = value;
			_isDemo = false;
		}
	}

	/// <summary>
	/// A second, innocent setting. The demo flag rewrites it, so anything that assigns the flag instead
	/// of reading it destroys what the caller asked for and can be seen doing so.
	/// </summary>
	public string Feed { get; set; } = "single-exchange";
}

/// <summary>
/// A connector that comes off demo willingly and is put back on it by one of its ordinary settings.
/// </summary>
/// <remarks>
/// The mirror image of <see cref="VenueAdapter"/>, and it exists for the mirror-image reason. A live
/// runner quietly moved onto a demo account is not dangerous the way the other direction is, and it is
/// dishonest in the same way: the state report would say it is trading money it is not, and the field
/// that said so would be one nobody re-read. So the flag is read again after the settings in both
/// directions, and this is the connector that makes the second direction fail without it.
/// </remarks>
public sealed class DemoingAdapter : MessageAdapter, IDemoAdapter
{
	private bool _isDemo = true;
	private string _venue = "live";

	/// <summary>
	/// Creates the adapter.
	/// </summary>
	/// <param name="ids">Generator the adapter numbers its transactions with.</param>
	public DemoingAdapter(IdGenerator ids)
		: base(ids)
	{
	}

	/// <inheritdoc />
	public bool IsDemo
	{
		get => _isDemo;
		set => _isDemo = value;
	}

	/// <summary>
	/// Which venue the adapter points at. Naming one puts the adapter back onto the demo host, which no
	/// rule refuses and which the read-back after the settings is the only thing that catches.
	/// </summary>
	public string Venue
	{
		get => _venue;
		set
		{
			_venue = value;
			_isDemo = true;
		}
	}
}
