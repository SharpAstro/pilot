using Pilot.Cli;

// pilot v2's command line: what the controller, the watchtower, workers and scripts use against the node.
return await PilotCommand.RunAsync(args, Environment.CurrentDirectory, Console.Out, Console.Error, CancellationToken.None);
