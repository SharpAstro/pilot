using Pilot.Node;

// pilot-node: the node by default; `hook` runs inside every Claude Code session on each hook, `hooks` installs them.
return await NodeCommandLine.RunAsync(args, Console.OpenStandardInput(), Console.Out, Console.Error, CancellationToken.None);
