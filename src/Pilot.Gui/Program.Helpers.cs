// The helpers of Program.cs that capture none of its top-level locals, as static methods of the class its top-level statements
// compile into (the global Program, so no namespace here). The rest of Program.cs's local functions close over those locals (the
// node client, the tracker, the chrome, the loop), so they stay where the locals are.
internal partial class Program
{
    // A say's description ("refused: ...") without its prefix: the view puts its own before a refusal.
    static string? Plain(string said) => said.StartsWith("refused: ", StringComparison.Ordinal) ? said["refused: ".Length..] : said;
}
