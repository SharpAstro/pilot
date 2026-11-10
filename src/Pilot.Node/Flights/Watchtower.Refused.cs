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
        if (reason == PersonSaidNo)
        {
            // The person refused it themselves (or interrupted while it waited on them): they know, and nothing rings.
            return;
        }
        var verb = call.Groups["verb"].Value.ToLowerInvariant();
        // Only pilot's own words: what follows a shell operator (2>&1, && …, | tail) is another command's.
        var rest = OwnWords(call.Groups["rest"].Value);
        var at = TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
        if (verb == "answer" && AnswerArgs().Match(rest) is { Success: true } answer
            && state.Cards(openOnly: true).FirstOrDefault(c => c.Id == answer.Groups["card"].Value) is { } card)
        {
            // The choice by its number or its label, as pilot answer takes it; words that name no button name the buttons there are.
            var choice = answer.Groups["choice"].Value.Trim().Trim('"', '\'');
            var label = int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= card.Choices.Length
                ? card.Choices[n - 1].Label
                : card.Choices.FirstOrDefault(c => string.Equals(c.Label, choice, StringComparison.OrdinalIgnoreCase))?.Label;
            var press = label is not null ? $"press \"{label}\" yourself"
                : card.Choices.Length > 0 ? $"press the button it chose yourself ({string.Join(", ", card.Choices.Select(c => $"\"{c.Label}\""))})"
                : "answer it yourself";
            var was = card.Title.StartsWith(RefusedPrefix, StringComparison.Ordinal) ? card.Title[RefusedPrefix.Length..] : card.Title;
            var detail = WithoutLeads(card.Detail);
            state.UpdateCard(card.Id, RefusedPrefix + was,
                $"{WhatToDo}{press}. The watchtower pressed it at {at} and {reason} refused it, so nothing moves until you do."
                    + (string.IsNullOrWhiteSpace(detail) ? "" : "\n\n" + detail),
                card.Choices, card.Link, bin: CardBin.Ring);
            LogStream($"the watchtower's press {label ?? choice} on {card.Id} was refused ({reason}): the card rings");
            return;
        }
        // A group's verb names its command too ("pilot flight merge", not "pilot flight"), and the card is one per command and what it acts
        // on: a second flight's refused merge is a card of its own, the same one refused again is the open card.
        var name = Groups.Contains(verb) && SubVerb().Match(rest) is { Success: true } sub ? $"{verb} {sub.Groups["sub"].Value}" : verb;
        var words = rest.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var first = name == verb ? 0 : 1;
        var on = words.Length > first && !words[first].StartsWith('-') ? "/" + words[first].Trim('"', '\'') : "";
        // The one press that moves it once the person has done it: the watchtower hears so and goes on from the result.
        IReadOnlyList<CardChoice> choices = Current() is { } tower
            ? [new CardChoice { Label = DoneMyselfLabel, Verb = Verbs.Say, Target = Verbs.SessionTarget(tower.Id), Args = [$"The person did it themselves after Claude Code refused your `pilot {name}`: carry on from what it did (pilot status, pilot cards)."] }]
            : [];
        state.RaiseCard(choices.Count > 0 ? CardBin.Ring : CardBin.ForYou, RefusedKind, $"{Verbs.WatchtowerTarget}/refused/{name.Replace(' ', '-')}{on}",
            $"The watchtower could not run pilot {name}: {reason} refused it",
            $"{WhatToDo}run it yourself, or do what it was for another way, then press \"{DoneMyselfLabel}\". Nothing it was meant to move moves until then.\n\nIt tried, at {at}:\n{command.Trim()}",
            choices);
        LogStream($"the watchtower's pilot {name} was refused ({reason}): a card rings");
    }

    /// <summary>The button of a refused command's card: the person did it, and the watchtower is told to go on.</summary>
    public const string DoneMyselfLabel = "Done it myself: tell the watchtower";

    // TranscriptBook.DenialOf's words for the person's own refusal.
    private const string PersonSaidNo = "you said no";

    // The verbs of pilot whose next word is a command of their own (pilot --help).
    private static readonly HashSet<string> Groups = new(StringComparer.Ordinal)
    {
        "session", "issue", "idea", "ideas", "flight", "leg", "check", "artifact", "review", "job", "watchtower", "node", "settings", "grants", "logs", "strays", "mcp", "orders", "order",
    };

    // The words up to the first unquoted shell operator (; & | < > or a line break).
    private static string OwnWords(string rest)
    {
        var quote = '\0';
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (quote != '\0')
            {
                quote = c == quote ? '\0' : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is ';' or '&' or '|' or '<' or '>' or '\r' or '\n')
            {
                // A redirect's own digit (2>&1) goes with it.
                var end = i > 0 && char.IsDigit(rest[i - 1]) && c == '>' ? i - 1 : i;
                return rest[..end].TrimEnd();
            }
        }
        return rest.TrimEnd();
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
