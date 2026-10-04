namespace Odysseus.Broker;

using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Security;

/// <summary>
/// Reads what an adapter declares, and configures one from what the caller asked for.
/// </summary>
/// <remarks>
/// Not through <c>IPersistable.Load</c>, although every adapter has it. That path silently ignores a key
/// it does not know and silently leaves a default for one that is missing, so a typo in a connector file
/// would be invisible - the server would connect, download a fraction of what was asked for, and say
/// nothing. Applied by reflection instead, and a name the adapter does not declare is refused with the
/// list of names it does.
///
/// The same reflection pass answers <c>describe_connector</c>, so what a caller is told the connector
/// accepts is exactly what selecting it will accept.
/// </remarks>
internal static class AdapterConfigurator
{
	/// <summary>
	/// Properties an adapter carries because of a capability interface rather than as a setting. They are
	/// applied from the credential file and from the paper guard, and are refused as settings.
	/// </summary>
	private static readonly string[] _notSettings =
	[
		"Key", "Secret", "Login", "Password", "Token", "Passphrase", "IsDemo",
	];

	/// <summary>
	/// Refuses a choice that would use the settings pass as a lever on the paper guarantee.
	/// </summary>
	/// <param name="choice">What the caller asked for.</param>
	/// <exception cref="ConnectorRefusedException">A setting names one of the capability properties.</exception>
	/// <remarks>
	/// Checked before anything is downloaded, because the answer does not depend on which connector it
	/// is: no connector may be told through a settings dictionary whether it is on a real account.
	/// </remarks>
	public static void Validate(ConnectorChoice choice)
	{
		ArgumentNullException.ThrowIfNull(choice);

		if (choice.Settings is null)
			return;

		foreach (var name in choice.Settings.Keys)
		{
			if (_notSettings.Contains(name, StringComparer.OrdinalIgnoreCase))
			{
				throw new ConnectorRefusedException(
					$"'{name}' is not a setting. Credentials come from the file ODYSSEUS_BROKER_KEYS names, and " +
					"whether the connector is on a paper account is not the caller's to say: this server puts " +
					"every connector into demo mode and refuses one that will not go.");
			}
		}
	}

	/// <summary>
	/// Applies the guard the process's mandate calls for, the credentials and the caller's settings, in
	/// that order.
	/// </summary>
	/// <param name="adapter">The adapter to configure.</param>
	/// <param name="credentials">What the credential file held, or null when there was none.</param>
	/// <param name="settings">Settings by property name, with the reserved keys already removed.</param>
	/// <param name="mandate">
	/// Which account this process may reach, as its start-up configuration decided. Required rather than
	/// defaulted: a default here would be a silent choice of whose money is at risk, and every call site
	/// says which of the two it means.
	/// </param>
	/// <exception cref="ConnectorNotPaperException">The adapter cannot be proven to be on the account the mandate names.</exception>
	/// <exception cref="ConnectorRefusedException">A credential or a setting does not fit the adapter.</exception>
	public static void Apply(
		IMessageAdapter adapter,
		BrokerCredentials credentials,
		IReadOnlyDictionary<string, string> settings,
		TradingMandate mandate)
	{
		ArgumentNullException.ThrowIfNull(adapter);
		ArgumentNullException.ThrowIfNull(mandate);

		// First, because setting the flag has side effects on some connectors and the settings below must
		// be the ones that survive. Which of the two guards runs is decided by the mandate this process was
		// born with, and by nothing that reached it afterwards.
		if (mandate.IsLive)
			LiveTradingGuard.Assert(adapter, mandate);
		else
			PaperOnlyGuard.Assert(adapter);

		ApplyCredentials(adapter, credentials);

		var declared = Own(adapter.GetType()).ToDictionary(p => p.Name, StringComparer.Ordinal);

		foreach (var (name, value) in settings ?? new Dictionary<string, string>())
		{
			if (!declared.TryGetValue(name, out var property))
			{
				throw new ConnectorRefusedException(
					$"{adapter.GetType().FullName} declares no setting called '{name}'. It declares: " +
					$"{string.Join(", ", declared.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
			}

			try
			{
				property.SetValue(adapter, Convert(value, property.PropertyType));
			}
			catch (Exception error) when (error is not ConnectorRefusedException)
			{
				throw new ConnectorRefusedException(
					$"'{value}' is not a value {adapter.GetType().FullName}.{name} accepts; it is declared as " +
					$"{Written(property.PropertyType)}. {error.Message}");
			}
		}

		// Read, not written: a connector whose flag was clobbered by another setting is refused here rather
		// than quietly put back where it was over the settings just applied. It runs in both directions,
		// so a setting can move a runner neither onto a real account nor off one - which is what keeps a
		// state report from being made to lie about which account is being traded.
		if (mandate.IsLive)
			LiveTradingGuard.Confirm(adapter);
		else
			PaperOnlyGuard.Confirm(adapter);

		if (adapter.ExtraSetup)
		{
			throw new ConnectorRefusedException(
				$"{adapter.GetType().FullName} says a configuration file is not enough to set it up, so this " +
				"server cannot drive it. Choose a connector that is configured by its settings alone.");
		}
	}

	/// <summary>
	/// Everything an adapter type declares as a setting.
	/// </summary>
	/// <param name="type">The adapter type.</param>
	/// <returns>The settings, ordered by name.</returns>
	public static IReadOnlyList<ConnectorSetting> Describe(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);

		return
		[
			.. Own(type)
				.OrderBy(p => p.Name, StringComparer.Ordinal)
				.Select(p => new ConnectorSetting(
					p.Name,
					Written(p.PropertyType),
					Explain(p),
					p.GetCustomAttribute<RequiredAttribute>() is not null,
					IsSecret(p),
					Allowed(p.PropertyType))),
		];
	}

	/// <summary>
	/// Which credentials an adapter type takes.
	/// </summary>
	/// <param name="type">The adapter type.</param>
	/// <returns>The shape, as the error contract names it.</returns>
	public static string CredentialShapeOf(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);

		if (typeof(IKeySecretAdapter).IsAssignableFrom(type))
			return "key-secret";

		if (typeof(ILoginPasswordAdapter).IsAssignableFrom(type))
			return "login-password";

		if (typeof(ITokenAdapter).IsAssignableFrom(type))
			return "token";

		return typeof(IPassphraseAdapter).IsAssignableFrom(type) ? "passphrase" : "none";
	}

	private static void ApplyCredentials(IMessageAdapter adapter, BrokerCredentials credentials)
	{
		var shape = CredentialShapeOf(adapter.GetType());

		if (credentials is null)
		{
			if (shape == "none")
				return;

			throw new ConnectorRefusedException(
				$"{adapter.GetType().FullName} needs {shape} credentials and none were supplied. Point " +
				"ODYSSEUS_BROKER_KEYS at a file holding them.");
		}

		switch (credentials.Kind)
		{
			case BrokerCredentialKinds.KeySecret when adapter is IKeySecretAdapter keyed:
				keyed.Key = credentials.Value("key").Secure();
				keyed.Secret = credentials.Value("secret").Secure();
				return;

			case BrokerCredentialKinds.LoginPassword when adapter is ILoginPasswordAdapter named:
				named.Login = credentials.Value("login");
				named.Password = credentials.Value("password").Secure();
				return;

			case BrokerCredentialKinds.Token when adapter is ITokenAdapter tokened:
				tokened.Token = credentials.Value("token").Secure();
				return;

			case BrokerCredentialKinds.Passphrase when adapter is IPassphraseAdapter phrased:
				phrased.Passphrase = credentials.Value("passphrase").Secure();
				return;

			default:
				throw new ConnectorRefusedException(
					$"The credential file holds {Name(credentials.Kind)} and " +
					$"{adapter.GetType().FullName} takes {shape}. Supply the credentials this connector asks " +
					"for, or choose a connector that takes the ones you have.");
		}
	}

	private static string Name(BrokerCredentialKinds kind)
		=> kind switch
		{
			BrokerCredentialKinds.KeySecret => "a key and a secret",
			BrokerCredentialKinds.LoginPassword => "a login and a password",
			BrokerCredentialKinds.Token => "a token",
			BrokerCredentialKinds.Passphrase => "a passphrase",
			_ => "nothing usable",
		};

	/// <summary>
	/// The properties an adapter declares of its own, as opposed to the hundred the platform's base class
	/// carries. Walking the chain and stopping at the base class is what tells the two apart.
	/// </summary>
	private static IEnumerable<PropertyInfo> Own(Type type)
	{
		var stop = typeof(MessageAdapter);

		for (var current = type; current is not null && current != stop && current != typeof(object); current = current.BaseType)
		{
			var declared = current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

			foreach (var property in declared)
			{
				if (property.GetIndexParameters().Length > 0)
					continue;

				if (property.GetSetMethod() is not { IsPublic: true })
					continue;

				if (_notSettings.Contains(property.Name, StringComparer.Ordinal))
					continue;

				yield return property;
			}
		}
	}

	private static object Convert(string value, Type type)
	{
		if (type == typeof(string))
			return value;

		if (type == typeof(SecureString))
			return value.Secure();

		var element = ElementOf(type);

		if (element is null)
			return value.To(type);

		var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var array = Array.CreateInstance(element, parts.Length);

		for (var i = 0; i < parts.Length; i++)
			array.SetValue(parts[i].To(element), i);

		return array;
	}

	/// <summary>
	/// The element type of a property that takes a list, or null when it takes a single value. An array
	/// of that element must be assignable to the property, or the caller would be handed something the
	/// setter refuses.
	/// </summary>
	private static Type ElementOf(Type type)
	{
		if (type == typeof(string))
			return null;

		if (type.IsArray)
			return type.GetElementType();

		if (!type.IsGenericType || type.GetGenericArguments().Length != 1)
			return null;

		var element = type.GetGenericArguments()[0];

		return type.IsAssignableFrom(element.MakeArrayType()) ? element : null;
	}

	private static IReadOnlyList<string> Allowed(Type type)
	{
		var subject = ElementOf(type) ?? type;
		var underlying = Nullable.GetUnderlyingType(subject) ?? subject;

		return underlying.IsEnum ? Enum.GetNames(underlying) : Array.Empty<string>();
	}

	private static bool IsSecret(PropertyInfo property)
		=> property.PropertyType == typeof(SecureString);

	private static string Written(Type type)
	{
		var element = ElementOf(type);

		return element is null ? type.Name : $"a list of {element.Name}";
	}

	/// <summary>
	/// What the adapter says the setting is for. The text lives in the connector's own resources, and a
	/// connector with none simply has nothing to say - which is not a reason to refuse it.
	/// </summary>
	private static string Explain(PropertyInfo property)
	{
		var display = property.GetCustomAttribute<DisplayAttribute>();

		if (display is null)
			return string.Empty;

		try
		{
			return display.GetDescription() ?? display.GetName() ?? string.Empty;
		}
		catch (InvalidOperationException)
		{
			// The attribute names a resource the connector did not ship, which says nothing about whether
			// the setting works.
			return string.Empty;
		}
	}
}
