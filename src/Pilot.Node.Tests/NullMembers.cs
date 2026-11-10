using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using Pilot.Contracts;

namespace Pilot.Node.Tests;

/// <summary>
/// Finds the members of a contract record that the type does not allow to be null and that are null: <c>AdmissionRow.WaitingOnPerson</c>
/// read from a store written before it existed, which stopped every node that updated onto beta 1718. The JSON reader sets every init
/// property from the file, a missing one to null whatever its initialiser says, and writing that row back is refused
/// (<c>RespectNullableAnnotations</c>), so a node whose store held such a row failed to start.
/// </summary>
/// <remarks>
/// Reads the nullable annotations through <see cref="NullabilityInfoContext"/>, walking records, arrays and dictionaries of the
/// contracts' assembly. Only members a reader can set are looked at (computed ones are not stored), and a member the serializer never
/// writes (<c>[JsonIgnore]</c>) is left out. A non-nullable array or dictionary entry that is null is named as <c>Type.Member[]</c>.
/// </remarks>
public static class NullMembers
{
    private static readonly Assembly Contracts = typeof(NodeEvent).Assembly;

    /// <summary>Each <c>Type.Member</c> under <paramref name="roots"/> that is null where its type does not allow it, in order, once each.</summary>
    public static IReadOnlyList<string> Find(params object?[] roots)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var context = new NullabilityInfoContext();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var root in roots)
        {
            if (root is not null)
            {
                WalkValue(root, null, "", context, found, seen);
            }
        }
        return [.. found];
    }

    private static void WalkMembers(object record, NullabilityInfoContext context, SortedSet<string> found, HashSet<object> seen)
    {
        if (!record.GetType().IsValueType && !seen.Add(record))
        {
            return;
        }

        foreach (var property in record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0 || property.SetMethod is not { IsPublic: true }
                || property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
            {
                continue;
            }

            var info = context.Create(property);
            var member = $"{property.DeclaringType!.Name}.{property.Name}";
            var value = property.GetValue(record);
            if (value is null)
            {
                if (info.ReadState == NullabilityState.NotNull)
                {
                    found.Add(member);
                }
                continue;
            }
            WalkValue(value, info, member, context, found, seen);
        }
    }

    private static void WalkValue(object value, NullabilityInfo? info, string member, NullabilityInfoContext context, SortedSet<string> found, HashSet<object> seen)
    {
        switch (value)
        {
            case string:
                return;
            case IDictionary dictionary:
            {
                var entry = info?.GenericTypeArguments is { Length: 2 } args ? args[1] : null;
                foreach (var item in dictionary.Values)
                {
                    WalkItem(item, entry, member + "[]", context, found, seen);
                }
                return;
            }
            case IEnumerable items:
            {
                var entry = info?.ElementType ?? (info?.GenericTypeArguments is { Length: 1 } args ? args[0] : null);
                foreach (var item in items)
                {
                    WalkItem(item, entry, member + "[]", context, found, seen);
                }
                return;
            }
        }

        if (value.GetType().Assembly == Contracts)
        {
            WalkMembers(value, context, found, seen);
        }
    }

    private static void WalkItem(object? item, NullabilityInfo? info, string member, NullabilityInfoContext context, SortedSet<string> found, HashSet<object> seen)
    {
        if (item is null)
        {
            if (info?.ReadState == NullabilityState.NotNull)
            {
                found.Add(member);
            }
            return;
        }
        WalkValue(item, info, member, context, found, seen);
    }
}
