using System.CommandLine;
using System.Globalization;
using LAN.Lib;
using Pilot.Contracts;

namespace Pilot.Commands;

public static partial class PilotCommand
{
    /// <summary>A time as typed: a full date and time, or a clock time (<c>14:30</c>), which is the next such time from <paramref name="now"/>.</summary>
    internal static DateTimeOffset ParseUntil(string text, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var at))
        {
            throw new UsageException($"cannot read '{text}' as a time; give 14:30 or 2026-10-27T09:00");
        }
        return TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out _) && at <= now ? at.AddDays(1) : at;
    }

    /// <summary>
    /// The leg the folder <paramref name="cwd"/> works on: the flying leg of the flight whose plane holds it, and who is
    /// asking, its worker by name, so the black box says which worker ticked what.
    /// </summary>
    internal static (string Target, string Who) LegHere(SnapshotEvent snapshot, string cwd, string fallback = "cli", bool validating = false)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
        var flight = snapshot.Flights.FirstOrDefault(f => f.State != FlightState.Landed
            && folder.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison))
            ?? throw new UsageException("this folder is in no flight's plane; name the leg with --leg <code>/<leg>");
        if (validating)
        {
            var pending = snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Planned && l.Validation == LegValidation.Pending)
                ?? throw new UsageException($"{flight.Code} has no leg waiting for a validation; name it with --leg {flight.Code}/<leg>");
            return (Verbs.LegTarget(flight.Code, pending.Name), pending.Validator ?? fallback);
        }
        var leg = snapshot.Legs.FirstOrDefault(l => l.Flight == flight.Code && l.State == LegState.Flying)
            ?? throw new UsageException($"{flight.Code} has no leg flying; name it with --leg {flight.Code}/<leg>");
        return (Verbs.LegTarget(flight.Code, leg.Name), leg.Worker ?? fallback);
    }
    private sealed partial class BuildContext
    {
        /// <summary>Makes the leg, check and artifact: the commands a leg's worker runs on its own leg.</summary>
        internal (Command, Command, Command) LegCommands()
        {
            // leg
            var name = Arg("name", "The leg's name: lower-case letters, digits and dashes");
            var goal = new Option<string>("--goal") { Description = "What the leg is for", Required = true };
            var doneWhen = new Option<string>("--done-when") { Description = "When it is done, in words", Required = true };
            var check = new Option<string?>("--check") { Description = "A command the node reruns in the plane before it takes the leg as done" };
            var ciDiffers = new Option<string?>("--ci-differs") { Description = "Why the check cannot build the way the repo's CI does (a dotnet build/test without CI's -c is refused otherwise)" };
            var wholeSuite = new Option<string?>("--whole-suite") { Description = "Why the check runs a whole test project with no --filter (refused otherwise: the whole suite is CI's, on the PR)" };
            var budget = new Option<string?>("--budget") { Description = "Its budget in weighted tokens" };
            var model = new Option<string?>("--model") { Description = "The model its workers run" };
            var effort = new Option<string?>("--effort") { Description = "The effort its workers run at" };
            var item = new Option<string[]>("--item") { Description = "A checklist step; give it once per step", AllowMultipleArgumentsPerToken = false };
            var plan = Action(new Command("plan", "Plan a leg at the end of the flight's order")
                {
                    Arguments = { code, name },
                    Options = { goal, doneWhen, check, ciDiffers, wholeSuite, budget, model, effort, item },
                },
                (p, client, ct) =>
                {
                    var args = new List<string> { $"name={p.GetValue(name)}", $"goal={p.GetValue(goal)}", $"done-when={p.GetValue(doneWhen)}" };
                    foreach (var (option, key) in (ReadOnlySpan<(Option<string?>, string)>)[(check, "check"), (ciDiffers, "ci-differs"), (wholeSuite, "whole-suite"), (budget, "budget"), (model, "model"), (effort, "effort")])
                    {
                        if (p.GetValue(option) is { } value)
                        {
                            args.Add($"{key}={value}");
                        }
                    }
                    args.AddRange((p.GetValue(item) ?? []).Select(i => $"item={i}"));
                    return Send(client, Verbs.PlanLeg, Verbs.FlightTarget(p.GetValue(code)!), [.. args], who, ct);
                });

            var legName = Arg("leg", "The leg's name");
            var start = Action(new Command("start", "Start a fresh worker on the leg, in the flight's plane") { Arguments = { code, legName } },
                (p, client, ct) => Send(client, Verbs.StartLeg, Verbs.LegTarget(p.GetValue(code)!, p.GetValue(legName)!), [], who, ct));

            var removeWhy = Words("why", "Why it goes: said on the card the removal leaves", required: false);
            var remove = Action(new Command("remove", "Take a planned leg out of the flight; the legs after it keep their order") { Arguments = { code, legName, removeWhy } },
                (p, client, ct) => Send(client, Verbs.RemoveLeg, Verbs.LegTarget(p.GetValue(code)!, p.GetValue(legName)!), p.GetValue(removeWhy) ?? [], who, ct));

            var outcome = Arg("outcome", "done, failed, retired (a leg stopped on the controller's or the owner's word: done in the row, its report starting retired:, no card; never failed), or accepted (the controller's: a failed leg marked done anyway, its failure kept in its report, so the flight goes on), or verified (anyone's: the node finishes a failed leg only if its plane matches its pushed branch and its check passes); pushed-with-lease and merged-with-origin are the controller's, for a blocked push");
            outcome.AcceptOnlyFromAmong("done", "failed", "retired", "accepted", "verified", "pushed-with-lease", "merged-with-origin");
            var report = Words("report", "A line on what was done, or why not", required: false);
            var finish = Action(new Command("finish", "Finish the leg: done reruns its check; failed rings for a go-around, unless its work is pushed and checks out, when the node finishes it") { Arguments = { outcome, report }, Options = { leg } },
                (p, client, ct) => OnLeg(p, client, Verbs.FinishLeg, _ => [p.GetValue(outcome)!, .. p.GetValue(report) ?? []], ct));

            var verdict = Arg("outcome", "go (the brief is still true of the tree) or no-go (a fact it rests on is stale)");
            verdict.AcceptOnlyFromAmong("go", "no-go");
            var validateReport = Words("report", "The facts checked (go), or each stale fact with the command that showed it (no-go)", required: false);
            var validate = Action(new Command("validate", "Answer as a leg's validator: go starts its worker; no-go rings with the stale facts") { Arguments = { verdict, validateReport }, Options = { leg } },
                (p, client, ct) => OnLeg(p, client, Verbs.ValidateLeg, _ => [p.GetValue(verdict)!, .. p.GetValue(validateReport) ?? []], ct));

            var why = Words("why", "Why: the fresh worker is told it");
            var goAround = Action(new Command("go-around", "Stop the leg's worker and start a fresh one, told why") { Arguments = { why }, Options = { leg } },
                (p, client, ct) => OnLeg(p, client, Verbs.GoAround, _ => [Join(p, why)], ct));

            var forFile = new Option<string?>("--for-file") { Description = "A file whose appearance wakes the worker (a relative path is from here)" };
            var until = new Option<string?>("--until") { Description = "A time: alone it wakes the worker then; with --for-file it rings once past it and goes on waiting (14:30, or 2026-10-27T09:00)" };
            var waitWhy = Words("why", "Why it waits: the wake-up says it back", required: false);
            var wait = Action(new Command("wait", "Wait on a file or a time with no job: end your turn after it, the node wakes you once it comes") { Arguments = { waitWhy }, Options = { forFile, until, leg } },
                (p, client, ct) =>
                {
                    var args = new List<string>();
                    if (p.GetValue(forFile) is { Length: > 0 } file)
                    {
                        args.Add($"path={(Path.IsPathRooted(file) ? file : Path.GetFullPath(file, cwd))}");
                    }
                    if (p.GetValue(until) is { Length: > 0 } at)
                    {
                        args.Add($"until={ParseUntil(at, DateTimeOffset.Now).ToString("o", System.Globalization.CultureInfo.InvariantCulture)}");
                    }
                    args.AddRange(p.GetValue(waitWhy) ?? []);
                    return OnLeg(p, client, Verbs.WaitLeg, _ => [.. args], ct);
                });

            var tokens = Arg("tokens", "The new budget in weighted tokens, as 6000000");
            var extend = Action(new Command("extend", "Raise the leg's budget") { Arguments = { tokens }, Options = { leg } },
                (p, client, ct) => OnLeg(p, client, Verbs.ExtendBudget, _ => [p.GetValue(tokens)!], ct));

            var legCommand = new Command("leg", "Legs: plan, remove, start, validate, finish, wait, go around, extend") { Subcommands = { plan, remove, start, validate, finish, wait, goAround, extend } };

            // check, artifact
            var step = Words("step", "The step's number, or the start of its text");
            var undo = new Option<bool>("--undo") { Description = "Untick it instead" };
            var checkCommand = Action(new Command("check", "Tick a step of the leg's checklist") { Arguments = { step }, Options = { undo, leg } },
                (p, client, ct) => OnLeg(p, client, Verbs.Check, _ => p.GetValue(undo) ? [Join(p, step), "undo"] : [Join(p, step)], ct));

            var location = Arg("location", "A path or a URL");
            var artifactTitle = Words("title", "What it shows", required: false);
            var artifact = Action(new Command("artifact", "Record something to look at on the leg") { Arguments = { location, artifactTitle }, Options = { leg } },
                async (p, client, ct) =>
                {
                    var where = p.GetValue(location)!;
                    var full = Path.IsPathRooted(where) || where.Contains("://", StringComparison.Ordinal) ? where : Path.GetFullPath(where, cwd);
                    if (p.GetValue(leg) is null)
                    {
                        // Outside every plane the artifact goes on the Claude session running this, which a flight's leg would not be.
                        var snapshot = await SnapshotAsync(client, ct);
                        var here = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)) + Path.DirectorySeparatorChar;
                        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        var inPlane = snapshot.Flights.Any(f => f.State != FlightState.Landed
                            && here.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.Worktree)) + Path.DirectorySeparatorChar, comparison));
                        if (!inPlane && sessionId is { Length: > 0 }
                            && snapshot.Sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) is { } session)
                        {
                            return await Send(client, Verbs.AddArtifact, Verbs.SessionTarget(session.Id), [full, .. p.GetValue(artifactTitle) ?? []], who, ct);
                        }
                    }
                    return await OnLeg(p, client, Verbs.AddArtifact, _ => [full, .. p.GetValue(artifactTitle) ?? []], ct);
                });

            return (legCommand, checkCommand, artifact);
        }
    }
}
