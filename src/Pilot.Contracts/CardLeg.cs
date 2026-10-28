namespace Pilot.Contracts;

/// <summary>The leg a card is about, as a flight code and a leg name: what a client links and <c>pilot cards</c> prints as <c>&lt;code&gt;/&lt;leg&gt;</c>.</summary>
public readonly record struct CardLeg(string Code, string Leg)
{
    /// <summary><c>&lt;code&gt;/&lt;leg&gt;</c>, as it is written and typed.</summary>
    public override string ToString() => $"{Code}/{Leg}";

    /// <summary>
    /// The leg <paramref name="card"/> is about: from its subject (<c>flight/&lt;code&gt;/&lt;leg&gt;</c>) or, failing that, the target of
    /// its go-around choice (<see cref="Verbs.LegTarget"/>). Null for a card about no leg.
    /// </summary>
    public static CardLeg? Of(CardRow card)
    {
        if (Parse(card.Subject) is { } subject)
        {
            return subject;
        }
        foreach (var choice in card.Choices)
        {
            if (choice.Verb == Verbs.GoAround && Parse(choice.Target) is { } target)
            {
                return target;
            }
        }
        return null;
    }

    /// <summary>The leg a <c>flight/&lt;code&gt;/&lt;leg&gt;</c> target names; null for any other target.</summary>
    public static CardLeg? Parse(string? target)
    {
        if (target is null || !target.StartsWith(Verbs.FlightTargetPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = target[Verbs.FlightTargetPrefix.Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 && slash < rest.Length - 1 ? new CardLeg(rest[..slash], rest[(slash + 1)..]) : null;
    }
}
