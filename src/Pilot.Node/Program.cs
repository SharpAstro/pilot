using Pilot.Node;

// `pilot-node hook` runs inside every Claude Code session on each hook, and `pilot-node hooks` installs those; anything
// else is the node itself.
return args switch
{
    [HookCommands.HookArgument, .. var rest] => await HookCommands.HookAsync(rest, Console.OpenStandardInput()),
    ["hooks", .. var rest] => HookCommands.Hooks(rest, Environment.ProcessPath!, Console.Out, Console.Error),
    _ => await NodeProgram.RunAsync(args, Console.Error, CancellationToken.None),
};
