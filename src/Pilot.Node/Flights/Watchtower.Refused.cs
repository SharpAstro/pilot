using System.Globalization;
using System.Text.RegularExpressions;
using Pilot.Contracts;

namespace Pilot.Node.Flights;

/// <summary>
/// A press or a pilot command of the watchtower that Claude Code refused (the auto mode classifier, a deny rule). The refusal is only
/// in the call's result, and the watchtower can only say so in its chat, which nobody reads at night: a card the ring judge left in For
/// you because "the watchtower can press it" stayed silent for seven hours (COD70, c9919, 2026-11-10). So a refused press moves that card
/// to the ring as a warning that names the button, and any other refused pilot command rings a card of its own.
/// </summary>
public sealed partial class Watchtower
{
    /// <summary>The ringing card of a pilot command of the watchtower that was refused, when it pressed no card's button.</summary>
    public const string RefusedKind = "watchtower-refused";

    /// <summary>What the title of a card whose press was refused starts with.</summary>
    public const string RefusedPrefix = "The watchtower's press was refused: ";

    private void Refused(string command, string reason)
    {
        if (PilotVerb().Match(command) is not { Success: true } call)
        {
            // Not the node's verb (a git call, a file outside its folder): the watchtower finds another way, and it is not a press.
            return;
        }
        var verb = call.Groups["verb"].Value;
        var at = TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
        if (verb == "answer" && AnswerArgs().Match(call.Groups["rest"].Value) is { Success: true } answer
            && state.Cards(openOnly: true).FirstOrDefault(c => c.Id == answer.Groups["card"].Value) is { } card)
        {
            // The choice by its number or its label, as pilot answer takes it.
            var choice = answer.Groups["choice"].Value.Trim().Trim('"', '\'');
            var label = int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n >= 1 && n <= card.Choices.Length ? card.Choices[n - 1].Label : $"choice {n}"
                : card.Choices.FirstOrDefault(c => string.Equals(c.Label, choice, StringComparison.OrdinalIgnoreCase))?.Label ?? choice;
            var was = card.Title.StartsWith(RefusedPrefix, StringComparison.Ordinal) ? card.Title[RefusedPrefix.Length..] : card.Title;
            var detail = WithoutLeads(card.Detail);
            state.UpdateCard(card.Id, RefusedPrefix + was,
                $"What to do: press \"{label}\" yourself. The watchtower pressed it at {at} and {reason} refused it, so nothing moves until you do."
                    + (string.IsNullOrWhiteSpace(detail) ? "" : "\n\n" + detail),
                card.Choices, card.Link, bin: CardBin.Ring);
            LogStream($"the watchtower's press of \"{label}\" on {card.Id} was refused ({reason}): the card rings");
            return;
        }
        // A group's verb names its command too: "pilot flight merge", not "pilot flight".
        var name = SubVerb().Match(call.Groups["rest"].Value) is { Success: true } sub ? $"{verb} {sub.Groups["sub"].Value}" : verb;
        state.RaiseCard(CardBin.Ring, RefusedKind, $"{Verbs.WatchtowerTarget}/refused/{name.Replace(' ', '-')}", $"The watchtower could not run pilot {name}: {reason} refused it",
            $"What to do: run it yourself, or answer what it was for another way. Nothing it was meant to move moves until then.\n\nIt tried, at {at}:\n{command.Trim()}");
        LogStream($"the watchtower's pilot {name} was refused ({reason}): a card rings");
    }

    // A detail without the paragraphs the node put in front of it: a quiet verdict, a ring's "What to do:", an earlier refusal's. So a
    // press refused twice, or a card the judge already rang, leads with one "What to do:".
    private static string? WithoutLeads(string? detail)
    {
        while (detail is not null && (detail.StartsWith(RingJudge.QuietPrefix, StringComparison.Ordinal) || detail.StartsWith(WhatToDo, StringComparison.Ordinal)))
        {
            detail = detail.IndexOf("\n\n", StringComparison.Ordinal) is var at and >= 0 ? detail[(at + 2)..] : null;
        }
        return detail;
    }

    private const string WhatToDo = "What to do: ";

    // The node's CLI as the command word (at the start, or after && ; | & ( or a line break, as PowerShell's & "…" calls it), by name or
    // by a path to it, with or without .exe and quotes, and its verb: "pilot answer c1 1", & "C:/…/pilot.exe" flight merge PIL1 abc.
    // Not a folder named pilot inside another command's words: "git -C C:/…/sharpastro/pilot push" is git's.
    [GeneratedRegex(@"(?:^|[;&|(\r\n])\s*(?:&\s*)?(?:""(?:[^""\r\n]*[/\\])?pilot(?:\.exe)?""|'(?:[^'\r\n]*[/\\])?pilot(?:\.exe)?'|(?:[^\s""';&|()]*[/\\])?pilot(?:\.exe)?)\s+(?<verb>[a-z][a-z-]*)(?<rest>[^\r\n]*)", RegexOptions.IgnoreCase)]
    private static partial Regex PilotVerb();

    [GeneratedRegex(@"^\s+(?<sub>[a-z][a-z-]*)(?=\s|$)")]
    private static partial Regex SubVerb();

    // The card and the choice, by its number or its label (quoted or not), before any option.
    [GeneratedRegex(@"^\s+(?<card>c\d+)\s+(?<choice>""[^""]*""|'[^']*'|\d+\b|[^-\s][^\r\n]*?)(?=\s+--|\s*$)")]
    private static partial Regex AnswerArgs();
}
