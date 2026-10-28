using Pilot.Node;

// pilot-node: the node by default; `hook` runs inside every Claude Code session on each hook, `hooks` installs them.
// Stderr here is the catastrophic fallback only: the node writes to <data folder>/logs/node.log (pilot #416), and what reaches
// this writer is a command line that cannot be read, a socket that cannot be used, or a log that cannot be written.
return await NodeCommandLine.RunAsync(args, Console.OpenStandardInput(), Console.Out, Console.Error, CancellationToken.None);
