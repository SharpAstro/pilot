using System.Globalization;
using DIR.Lib;
using Pilot.Contracts;

namespace Pilot.Ui;

/// <summary>What the node said to an idea verb, as the page needs it.</summary>
internal sealed record IdeaReply(bool Ok, string? Error = null, string? Way = null, string? Made = null, IdeaTidyProposal? Tidy = null, MilestoneProposal? Milestones = null, ReadinessResult? Readiness = null, MilestoneSuggestions? Suggestions = null, IdeaRow? Idea = null, BreakUpProposal? BreakUp = null);
