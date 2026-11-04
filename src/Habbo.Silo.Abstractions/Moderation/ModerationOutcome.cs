using Habbo.Domain.Moderation;

namespace Habbo.Silo.Abstractions.Moderation;

/// <summary>How a moderator's action went: done, or refused and why. The mod tool hears it as a packet (or not at
/// all); a website acting for a moderator reads it here.</summary>
public enum ModerationOutcome
{
    Done,

    /// <summary>No such user, room or issue - or no issue this moderator may act on, as one they did not pick.</summary>
    NotFound,

    /// <summary>The moderator may not do this.</summary>
    Permission,

    /// <summary>The user is of the moderator's rank, or above it.</summary>
    Rank,

    /// <summary>No such ban on the sanction ladder, or no such way to close an issue.</summary>
    Type,
}

/// <summary>The step of the ladder a default sanction gave; when refused, the step it would have been, where it got
/// that far.</summary>
public sealed record LadderSanction(ModerationOutcome Outcome, SanctionKind? Kind = null, int? SanctionTypeId = null);

/// <summary>An issue another moderator has picked, and who.</summary>
public sealed record TakenIssue(int IssueId, string PickerName);

/// <summary>The issues a moderator picked; none when another moderator has any of them, which are then named.</summary>
public sealed record IssuesPicked(ModerationOutcome Outcome, IReadOnlyList<int> Picked, IReadOnlyList<TakenIssue> Taken);

/// <summary>The issues a moderator closed: those they had picked.</summary>
public sealed record IssuesClosed(ModerationOutcome Outcome, IReadOnlyList<int> Closed);
