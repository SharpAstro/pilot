using System.Text.Json;
using Pilot.Node.Flights;

namespace Pilot.Node.Tests;

/// <summary>A clock the test moves: the fake <c>az</c> says how long each call took by advancing it, so a VM's minutes are exact.</summary>
internal sealed class CloudClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;
}

/// <summary>What a fake process was asked, and whether the caller's token was already cancelled when it was.</summary>
internal sealed record SeenCommand(CloudCommand Command, bool TokenCancelled)
{
    public string Line => Command.Line;
}

/// <summary>
/// <see cref="ICloudProcess"/> for the cloud bench tests: it answers the happy path of <c>az</c>, <c>ssh</c>, <c>scp</c>, <c>ssh-keygen</c> and
/// <c>git</c> by default, a test overrides the calls it is about (the latest rule that matches wins), and every command is kept, in order. A
/// command nobody has a rule for fails the test, so nothing runs that the test did not expect. No process is ever started.
/// </summary>
internal sealed class FakeCloud : ICloudProcess
{
    public const string PublicIp = "20.1.2.3";

    private readonly List<(Func<CloudCommand, bool> When, Func<CloudCommand, CancellationToken, CloudProcessRun> Then)> _rules = [];
    private readonly Lock _gate = new();
    private readonly List<SeenCommand> _seen = [];

    public CloudClock Clock { get; } = new();

    /// <summary>The script as it stood when it was copied to the VM (the file is deleted after).</summary>
    public string? ShippedScript { get; private set; }

    /// <summary>Every command the bench issued, in order.</summary>
    public IReadOnlyList<SeenCommand> Seen
    {
        get
        {
            lock (_gate)
            {
                return [.. _seen];
            }
        }
    }

    public IReadOnlyList<string> Lines => [.. Seen.Select(s => s.Line)];

    public FakeCloud()
    {
        On(c => c.Is("az", "account", "show"), _ => Ok());
        On(c => c.Is("az", "login"), _ => Ok());
        On(c => c.Program == "git", _ => Ok());
        On(c => c.Program == "ssh-keygen", c =>
        {
            File.WriteAllText(Path.Combine(c.WorkingDirectory!, "id_bench"), "PRIVATE KEY");
            File.WriteAllText(Path.Combine(c.WorkingDirectory!, "id_bench.pub"), "ssh-ed25519 AAAA pilot-bench");
            return Ok();
        });
        On(c => c.Is("az", "vm", "create"), _ =>
        {
            Clock.Advance(TimeSpan.FromMinutes(4));
            return Ok(JsonSerializer.Serialize(new { publicIpAddress = PublicIp, powerState = "VM running" }));
        });
        On(c => c.Is("az", "vm", "show"), _ => Ok("24.04.202411010\n"));
        On(c => c.Program == "ssh" && c.Remote() == "true", _ => Ok());
        On(c => c.Program == "scp" && !c.Args.Contains("-r"), c =>
        {
            ShippedScript = File.ReadAllText(Path.Combine(c.WorkingDirectory!, "bench.sh"));
            return Ok();
        });
        On(c => c.Program == "ssh" && c.Remote() == "bash bench.sh", c =>
        {
            c.OnLine?.Invoke("=== bench baseline: main (abc1234) ===");
            c.OnLine?.Invoke("| Mean | 1.00 ms |");
            Clock.Advance(TimeSpan.FromMinutes(30));
            return Ok();
        });
        On(c => c.Program == "scp" && c.Args.Contains("-r"), c =>
        {
            // The label of the folder asked for, copied into results/ as a bench would leave it.
            var label = c.Args.First(a => a.Contains(":out/", StringComparison.Ordinal)).Split(":out/")[1];
            var results = Path.Combine(c.WorkingDirectory!, "results", label, "BenchmarkDotNet.Artifacts", "results");
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "Sample.Bench-report-github.md"), $"| Method | Mean |\n| {label} | 1.00 ms |\n");
            return Ok();
        });
        On(c => c.Is("az", "vm", "get-instance-view"), _ => Ok("PowerState/running\n"));
        On(c => c.Is("az", "vm", "delete"), _ =>
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            return Ok();
        });
        // What is deleted after the VM costs the clock a good deal here, to show it is not part of the VM's minutes.
        On(c => c.Is("az", "network") && c.Args.Contains("delete") || c.Is("az", "disk", "delete"), _ =>
        {
            Clock.Advance(TimeSpan.FromMinutes(2));
            return Ok();
        });
        On(c => c.Is("az", "resource", "list"), _ => Ok(""));
        On(c => c.Is("az", "resource", "delete"), _ => Ok());
    }

    /// <summary>Adds a rule that wins over every earlier one.</summary>
    public FakeCloud On(Func<CloudCommand, bool> when, Func<CloudCommand, CloudProcessRun> then) => On(when, (c, _) => then(c));

    public FakeCloud On(Func<CloudCommand, bool> when, Func<CloudCommand, CancellationToken, CloudProcessRun> then)
    {
        lock (_gate)
        {
            _rules.Add((when, then));
        }
        return this;
    }

    public Task<CloudProcessRun> RunAsync(CloudCommand command, CancellationToken cancellationToken)
    {
        Func<CloudCommand, CancellationToken, CloudProcessRun>? answer = null;
        lock (_gate)
        {
            _seen.Add(new SeenCommand(command, cancellationToken.IsCancellationRequested));
            for (var i = _rules.Count - 1; i >= 0 && answer is null; i--)
            {
                if (_rules[i].When(command))
                {
                    answer = _rules[i].Then;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((answer ?? throw new InvalidOperationException($"no rule for the command {command.Line}"))(command, cancellationToken));
    }

    public static CloudProcessRun Ok(string output = "") => new(0, output, "");

    public static CloudProcessRun Fail(int exit, string error) => new(exit, "", error);

    /// <summary>What az says of a resource that is not there.</summary>
    public static CloudProcessRun NotFound() => Fail(3, "ERROR: (ResourceNotFound) The Resource 'Microsoft.Compute/virtualMachines/pilot-bench-j7-vm' under resource group 'rg-bench' was not found.");
}

internal static class CloudCommandExtensions
{
    /// <summary>Whether the command is <paramref name="program"/> followed by these words as its first arguments.</summary>
    public static bool Is(this CloudCommand command, string program, params string[] words) =>
        command.Program == program && command.Args.Count >= words.Length && words.Zip(command.Args).All(p => p.First == p.Second);

    /// <summary>What an ssh command runs on the other side: its last word.</summary>
    public static string? Remote(this CloudCommand command) => command.Program == "ssh" ? command.Args.LastOrDefault() : null;

    public static string After(this CloudCommand command, string option) => command.Args[command.Args.ToList().IndexOf(option) + 1];
}
