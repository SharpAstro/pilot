using Shouldly;
using Xunit;

namespace Pilot.Node.Tests;

/// <summary>
/// The largest files were split into partial classes by concern (PIL531), and this holds them there: every file matching one of
/// <see cref="Globs"/> stays at or under <see cref="MaxLines"/> lines, so a part that grows past it is split again rather than
/// becoming the next 3,000-line file. A later split adds its glob to the one array.
/// </summary>
public sealed class SplitFileSizeTests
{
    private const int MaxLines = 850;

    // Paths relative to the repo root (the folder holding src/Pilot.slnx), with * as the only wildcard, in the file name.
    private static readonly string[] Globs =
    [
        "src/Pilot.Node/Flights/LegCommander*.cs",
        "src/Pilot.Node/Flights/LegTools*.cs",
        "src/Pilot.Node/Flights/PrReviews*.cs",
        "src/Pilot.Node/Flights/FlightCommander*.cs",
        "src/Pilot.Node/Flights/Briefs*.cs",
        "src/Pilot.Node/NodeState*.cs",
        "src/Pilot.Node/NodeApp*.cs",
        "src/Pilot.Commands/PilotCommand*.cs",
        "src/Pilot.Ui/PullRequestsView*.cs",
        "src/Pilot.Ui/IdeasView*.cs",
        "src/Pilot.Ui/FlightsView*.cs",
    ];

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Pilot.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("no folder holding src/Pilot.slnx above the test's directory");
    }

    [Fact]
    public void Split_files_stay_under_the_size_limit()
    {
        var root = FindRoot();
        var tooLong = new List<string>();

        foreach (var glob in Globs)
        {
            var folder = Path.Combine(root, Path.GetDirectoryName(glob)!.Replace('/', Path.DirectorySeparatorChar));
            var files = Directory.EnumerateFiles(folder, Path.GetFileName(glob)).Order(StringComparer.Ordinal).ToList();
            files.ShouldNotBeEmpty($"{glob} matches no file");

            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file).Length;
                if (lines > MaxLines)
                {
                    tooLong.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')} has {lines} lines (limit {MaxLines})");
                }
            }
        }

        tooLong.ShouldBeEmpty("split these files further by concern:" + Environment.NewLine + string.Join(Environment.NewLine, tooLong));
    }
}
