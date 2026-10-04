namespace Odysseus.Broker;

using System.Linq;
using System.Reflection;

/// <summary>
/// What adapters an assembly holds.
/// </summary>
/// <remarks>
/// The two filters - a type is an adapter if it implements the platform's adapter interface, is not a
/// dialect, and has a public constructor taking an identifier generator - are the platform's own, and
/// are reimplemented here rather than referenced. The class that carries them in the platform installs
/// a process-wide assembly resolver in its static constructor and constructs one instance of every
/// adapter it finds merely to answer what exists; merely naming it would arm both. Twelve lines are
/// cheaper than either.
/// </remarks>
internal static class AdapterCatalog
{
	/// <summary>
	/// Finds the adapter types in an assembly.
	/// </summary>
	/// <param name="assembly">Assembly to read.</param>
	/// <returns>The adapters, ordered by full name.</returns>
	public static IReadOnlyList<Type> Find(Assembly assembly)
	{
		ArgumentNullException.ThrowIfNull(assembly);

		Type[] types;

		try
		{
			types = assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException partial)
		{
			// A package whose optional dependencies are absent still holds the adapter it was chosen for.
			types = [.. partial.Types.Where(t => t is not null)];
		}

		return
		[
			.. types
				.Where(IsAdapter)
				.OrderBy(t => t.FullName, StringComparer.Ordinal),
		];
	}

	/// <summary>
	/// Picks the one adapter a choice names, or the only one there is.
	/// </summary>
	/// <param name="assembly">Assembly the package brought.</param>
	/// <param name="typeName">Full or simple type name, or empty when the package holds one adapter.</param>
	/// <returns>The adapter type.</returns>
	/// <exception cref="ConnectorRefusedException">There is no such adapter, or there is more than one and none was named.</exception>
	public static Type Select(Assembly assembly, string typeName)
	{
		var found = Find(assembly);

		if (found.Count == 0)
		{
			throw new ConnectorRefusedException(
				$"{assembly.GetName().Name} holds no broker adapter, so there is nothing to connect with.");
		}

		if (string.IsNullOrWhiteSpace(typeName))
		{
			return found.Count == 1
				? found[0]
				: throw new ConnectorRefusedException(
					$"{assembly.GetName().Name} holds {found.Count} adapters and the choice named none of them. " +
					$"Name one of: {string.Join(", ", found.Select(t => t.FullName))}.");
		}

		return found.FirstOrDefault(t => t.FullName == typeName || t.Name == typeName)
			?? throw new ConnectorRefusedException(
				$"{assembly.GetName().Name} holds no adapter called '{typeName}'. It holds: " +
				$"{string.Join(", ", found.Select(t => t.FullName))}.");
	}

	/// <summary>
	/// Builds one adapter.
	/// </summary>
	/// <param name="type">Adapter type.</param>
	/// <param name="ids">Generator the adapter numbers its transactions with.</param>
	/// <returns>The adapter.</returns>
	public static IMessageAdapter Create(Type type, IdGenerator ids)
	{
		ArgumentNullException.ThrowIfNull(type);
		ArgumentNullException.ThrowIfNull(ids);

		return (IMessageAdapter)Constructor(type).Invoke([ids]);
	}

	private static bool IsAdapter(Type type)
		=> type is { IsAbstract: false, IsClass: true } &&
			typeof(IMessageAdapter).IsAssignableFrom(type) &&
			!type.Name.EndsWith("Dialect", StringComparison.Ordinal) &&
			Constructor(type) is not null;

	private static ConstructorInfo Constructor(Type type)
		=> type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, [typeof(IdGenerator)], null);
}
