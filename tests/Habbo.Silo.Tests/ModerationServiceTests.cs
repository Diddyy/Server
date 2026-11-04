using System.Collections.Concurrent;
using System.Reflection;
using Habbo.Domain.Moderation;
using Habbo.Domain.Rooms;
using Habbo.Domain.Users;
using Habbo.Infrastructure;
using Habbo.Protocol.Messages.Outgoing.Help;
using Habbo.Protocol.Messages.Outgoing.Moderation;
using Habbo.Silo.Abstractions.Hotel;
using Habbo.Silo.Abstractions.Moderation;
using Habbo.Silo.Abstractions.Permissions;
using Habbo.Silo.Abstractions.Players;
using Habbo.Silo.Abstractions.Rooms;
using Habbo.Silo.Moderation;
using Habbo.Silo.Permissions;
using Habbo.Silo.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;

namespace Habbo.Silo.Tests;

/// <summary>What the mod tool's service answers to whoever asked (the mod tool ignores it; the CMS bridge reports it):
/// done, or refused for the same reasons the mod tool is.</summary>
public class ModerationServiceTests
{
    private readonly Fixture _hotel = new();

    [Fact]
    public async Task ACautionIsGivenAndTheModeratorToldSo()
    {
        var target = _hotel.AddUser(SecurityLevel.AboveNone);

        var outcome = await _hotel.Service.SanctionAsync(_hotel.Moderator.Id, Action(SanctionKind.Alert, target));

        Assert.Equal(ModerationOutcome.Done, outcome);
        Assert.Single(_hotel.Db().Sanctions.Where(s => s.UserId == target.Id && s.Kind == SanctionKind.Alert));
        Assert.Contains(_hotel.Grains.SentTo(target.Id), m => m == new ModeratorCautionComposer("behave", string.Empty).Header);
    }

    [Fact]
    public async Task ASanctionIsRefusedForAUserOfTheModeratorsRankOrAbove()
    {
        var target = _hotel.AddUser(SecurityLevel.Moderator);

        Assert.Equal(ModerationOutcome.Rank, await _hotel.Service.SanctionAsync(_hotel.Moderator.Id, Action(SanctionKind.Kick, target)));
        Assert.Empty(_hotel.Db().Sanctions);
    }

    [Fact]
    public async Task ASanctionIsRefusedToWhoeverMayNotGiveIt()
    {
        var target = _hotel.AddUser(SecurityLevel.AboveNone);
        var player = _hotel.AddUser(SecurityLevel.Partner);

        Assert.Equal(ModerationOutcome.Permission, await _hotel.Service.SanctionAsync(player.Id, Action(SanctionKind.Alert, target)));
        // a ban asks for more than a caution does
        Assert.Equal(ModerationOutcome.Permission, await _hotel.Service.SanctionAsync(
            _hotel.Moderator.Id, Action(SanctionKind.Ban, target) with { SanctionTypeId = Fixture.BanTypeId }));
        Assert.Empty(_hotel.Db().Sanctions);
    }

    [Fact]
    public async Task ABanNeedsABanOfTheLadder()
    {
        var target = _hotel.AddUser(SecurityLevel.AboveNone);

        Assert.Equal(ModerationOutcome.Type, await _hotel.Service.SanctionAsync(
            _hotel.Administrator.Id, Action(SanctionKind.Ban, target) with { SanctionTypeId = Fixture.AlertTypeId }));
        Assert.Equal(ModerationOutcome.Done, await _hotel.Service.SanctionAsync(
            _hotel.Administrator.Id, Action(SanctionKind.Ban, target) with { SanctionTypeId = Fixture.BanTypeId }));
        Assert.Single(_hotel.Db().Sanctions.Where(s => s.Kind == SanctionKind.Ban && s.SanctionTypeId == Fixture.BanTypeId));
    }

    [Fact]
    public async Task NobodyByThatAccountIsNotFound() =>
        Assert.Equal(ModerationOutcome.NotFound, await _hotel.Service.SanctionAsync(
            _hotel.Moderator.Id, new ModerationAction(SanctionKind.Alert, 999_999, "behave", 0, null)));

    [Fact]
    public async Task AMessageFromAnIssueToItsReporterAnswersTheirCall()
    {
        var reporter = _hotel.AddUser(SecurityLevel.AboveNone);
        var issue = _hotel.AddIssue(reporter, reported: null);

        var outcome = await _hotel.Service.SanctionAsync(
            _hotel.Moderator.Id, Action(SanctionKind.Message, reporter) with { IssueId = issue.Id });

        Assert.Equal(ModerationOutcome.Done, outcome);
        Assert.Contains(_hotel.Grains.SentTo(reporter.Id), m => m == new CallForHelpReplyMessageComposer(string.Empty).Header);
    }

    [Fact]
    public async Task TheLadderSaysWhichStepItGave()
    {
        var target = _hotel.AddUser(SecurityLevel.AboveNone);

        var first = await _hotel.Service.DefaultSanctionAsync(_hotel.Moderator.Id, target.AccountId, 0, "behave", null);
        var second = await _hotel.Service.DefaultSanctionAsync(_hotel.Moderator.Id, target.AccountId, 0, "behave", null);

        Assert.Equal(new LadderSanction(ModerationOutcome.Done, SanctionKind.Alert, Fixture.AlertTypeId), first);
        Assert.Equal(new LadderSanction(ModerationOutcome.Done, SanctionKind.Mute, Fixture.MuteTypeId), second);
    }

    [Fact]
    public async Task AHotelWithoutALadderRefusesTheDefaultSanctionInsteadOfFailing()
    {
        var target = _hotel.AddUser(SecurityLevel.AboveNone);
        await using (var db = _hotel.Db())
        {
            db.SanctionTypes.RemoveRange(db.SanctionTypes);
            await db.SaveChangesAsync();
        }

        var outcome = await _hotel.Service.DefaultSanctionAsync(_hotel.Moderator.Id, target.AccountId, 0, "behave", null);

        Assert.Equal(new LadderSanction(ModerationOutcome.Type), outcome);
        Assert.Empty(_hotel.Db().Sanctions);
    }

    [Fact]
    public async Task TheLadderStopsAtABanTheModeratorMayNotGive()
    {
        var target = _hotel.AddUser(SecurityLevel.AboveNone);
        await _hotel.Service.DefaultSanctionAsync(_hotel.Moderator.Id, target.AccountId, 0, "behave", null);
        await _hotel.Service.DefaultSanctionAsync(_hotel.Moderator.Id, target.AccountId, 0, "behave", null);

        var third = await _hotel.Service.DefaultSanctionAsync(_hotel.Moderator.Id, target.AccountId, 0, "behave", null);

        Assert.Equal(new LadderSanction(ModerationOutcome.Permission, SanctionKind.Ban, Fixture.BanTypeId), third);
        Assert.Equal(2, _hotel.Db().Sanctions.Count());
        Assert.Equal(ModerationOutcome.Rank,
            (await _hotel.Service.DefaultSanctionAsync(_hotel.Moderator.Id, _hotel.Administrator.AccountId, 0, "behave", null)).Outcome);
    }

    [Fact]
    public async Task IssuesArePickedUnlessAnotherModeratorHasOne()
    {
        var reporter = _hotel.AddUser(SecurityLevel.AboveNone);
        var free = _hotel.AddIssue(reporter, reported: null);
        var theirs = _hotel.AddIssue(reporter, reported: null, pickedBy: _hotel.Administrator);

        var refused = await _hotel.Service.PickIssuesAsync(_hotel.Moderator.Id, [free.Id, theirs.Id], false, 0, string.Empty);
        var picked = await _hotel.Service.PickIssuesAsync(_hotel.Moderator.Id, [free.Id], false, 0, string.Empty);

        Assert.Empty(refused.Picked);
        Assert.Equal(new[] { new TakenIssue(theirs.Id, _hotel.Administrator.Username) }, refused.Taken);
        Assert.Equal(new[] { free.Id }, picked.Picked);
        Assert.Equal(_hotel.Moderator.Id, _hotel.Db().ModerationIssues.Single(i => i.Id == free.Id).PickerId);
        Assert.Equal(ModerationOutcome.Permission,
            (await _hotel.Service.PickIssuesAsync(reporter.Id, [free.Id], false, 0, string.Empty)).Outcome);
    }

    [Fact]
    public async Task OnlyTheModeratorsOwnIssuesAreClosedOrReleased()
    {
        var reporter = _hotel.AddUser(SecurityLevel.AboveNone);
        var mine = _hotel.AddIssue(reporter, reported: null, pickedBy: _hotel.Moderator);
        var alsoMine = _hotel.AddIssue(reporter, reported: null, pickedBy: _hotel.Moderator);
        var theirs = _hotel.AddIssue(reporter, reported: null, pickedBy: _hotel.Administrator);

        Assert.Equal(ModerationOutcome.Type, (await _hotel.Service.CloseIssuesAsync(_hotel.Moderator.Id, [mine.Id], 0)).Outcome);
        var closed = await _hotel.Service.CloseIssuesAsync(_hotel.Moderator.Id, [mine.Id, theirs.Id], (int)ModerationCloseReason.Useless);
        var released = await _hotel.Service.ReleaseIssuesAsync(_hotel.Moderator.Id, [alsoMine.Id, theirs.Id]);

        Assert.Equal(ModerationOutcome.Done, closed.Outcome);
        Assert.Equal(new[] { mine.Id }, closed.Closed);
        Assert.Equal(new[] { alsoMine.Id }, released);
        Assert.Equal(ModerationIssueState.Picked, _hotel.Db().ModerationIssues.Single(i => i.Id == theirs.Id).State);
    }

    [Fact]
    public async Task AnIssueIsResolvedWithTheLadderOnlyWhenTheSanctionIsGiven()
    {
        var reporter = _hotel.AddUser(SecurityLevel.AboveNone);
        var reported = _hotel.AddUser(SecurityLevel.AboveNone);
        var staff = _hotel.AddUser(SecurityLevel.Administrator);
        var issue = _hotel.AddIssue(reporter, reported, pickedBy: _hotel.Moderator);
        var aboutStaff = _hotel.AddIssue(reporter, staff, pickedBy: _hotel.Moderator);
        var unpicked = _hotel.AddIssue(reporter, reported);

        Assert.Equal(ModerationOutcome.NotFound, await _hotel.Service.CloseWithDefaultSanctionAsync(_hotel.Moderator.Id, unpicked.Id, [], 0));
        Assert.Equal(ModerationOutcome.Rank, await _hotel.Service.CloseWithDefaultSanctionAsync(_hotel.Moderator.Id, aboutStaff.Id, [], 0));
        Assert.Equal(ModerationOutcome.Done, await _hotel.Service.CloseWithDefaultSanctionAsync(_hotel.Moderator.Id, issue.Id, [], 0));

        var db = _hotel.Db();
        Assert.Equal(ModerationIssueState.Picked, db.ModerationIssues.Single(i => i.Id == aboutStaff.Id).State);
        Assert.True(db.ModerationIssues.Single(i => i.Id == issue.Id) is { State: ModerationIssueState.Closed, Sanctioned: true });
        Assert.Single(db.Sanctions.Where(s => s.UserId == reported.Id && s.IssueId == issue.Id));
    }

    [Fact]
    public async Task ARoomMessageGoesToTheRoomNamed()
    {
        var player = _hotel.AddUser(SecurityLevel.AboveNone);
        var room = _hotel.AddRoom();

        Assert.Equal(ModerationOutcome.Permission, await _hotel.Service.RoomMessageAsync(player.Id, room.Id, "hello", false, false));
        Assert.Equal(ModerationOutcome.NotFound, await _hotel.Service.RoomMessageAsync(_hotel.Moderator.Id, room.Id + 1, "hello", false, false));
        Assert.Empty(_hotel.Grains.Calls(nameof(IGameRoomGrain.StaffMessageAsync)));
        Assert.Equal(ModerationOutcome.Done, await _hotel.Service.RoomMessageAsync(_hotel.Moderator.Id, room.Id, "hello", true, true));

        var call = Assert.Single(_hotel.Grains.Calls(nameof(IGameRoomGrain.StaffMessageAsync)));
        Assert.Equal((long)room.Id, call.Key);
        Assert.Equal(new object?[] { "hello", true, true }, call.Args);
    }

    [Fact]
    public async Task AModeratedRoomIsRefusedToWhoeverMayNotEmptyIt()
    {
        var player = _hotel.AddUser(SecurityLevel.AboveNone);
        var room = _hotel.AddRoom();

        Assert.Equal(ModerationOutcome.Permission, await _hotel.Service.ModerateRoomAsync(player.Id, room.Id, true, false, true));
        Assert.Equal(ModerationOutcome.NotFound, await _hotel.Service.ModerateRoomAsync(_hotel.Moderator.Id, room.Id + 1, true, false, true));
        Assert.Equal(ModerationOutcome.Done, await _hotel.Service.ModerateRoomAsync(_hotel.Moderator.Id, room.Id, true, false, true));
        Assert.Single(_hotel.Grains.Calls(nameof(IGameRoomGrain.StaffModerateAsync)));
    }

    private static ModerationAction Action(SanctionKind kind, User target) => new(kind, target.AccountId, "behave", 0, null);

    /// <summary>A hotel of a database (in memory), its permissions, and grains that do nothing but note what they
    /// were asked: a moderator, an administrator, and the sanction ladder alert, mute, ban.</summary>
    private sealed class Fixture
    {
        public const int AlertTypeId = 1;
        public const int MuteTypeId = 2;
        public const int BanTypeId = 3;

        private readonly Factory _factory = new(Guid.NewGuid().ToString());
        private int _accountIds;

        public FakeGrains Grains { get; } = FakeGrains.Create();
        public ModerationService Service { get; }
        public User Moderator { get; }
        public User Administrator { get; }

        public Fixture()
        {
            using (var db = Db())
            {
                db.Permissions.AddRange(
                    new Permission { Name = PermissionNames.ModerationTool, MinimumRank = SecurityLevel.Moderator },
                    new Permission { Name = PermissionNames.ModerationCallForHelp, MinimumRank = SecurityLevel.Moderator },
                    new Permission { Name = PermissionNames.ModerationAlert, MinimumRank = SecurityLevel.Moderator },
                    new Permission { Name = PermissionNames.ModerationKick, MinimumRank = SecurityLevel.Moderator },
                    new Permission { Name = PermissionNames.ModerationRoomAlert, MinimumRank = SecurityLevel.Moderator },
                    new Permission { Name = PermissionNames.ModerationRoomKick, MinimumRank = SecurityLevel.Moderator },
                    new Permission { Name = PermissionNames.ModerationBan, MinimumRank = SecurityLevel.Administrator });
                db.SanctionTypes.AddRange(
                    new SanctionType { Id = AlertTypeId, Name = "ALERT", Kind = SanctionKind.Alert, Step = 1, ProbationDays = 30 },
                    new SanctionType { Id = MuteTypeId, Name = "MUTE", Kind = SanctionKind.Mute, Step = 2, LengthHours = 1, ProbationDays = 30 },
                    new SanctionType { Id = BanTypeId, Name = "BAN", Kind = SanctionKind.Ban, Step = 3, LengthHours = 24, ProbationDays = 30 });
                db.SaveChanges();
            }

            var permissions = new PermissionService(_factory, NullLogger<PermissionService>.Instance);
            permissions.ReloadAsync().GetAwaiter().GetResult();
            Service = new ModerationService(_factory, Grains.Factory, new PlayerMessenger(Grains.Factory), permissions,
                Options.Create(new ModerationOptions()));
            Moderator = AddUser(SecurityLevel.Moderator);
            Administrator = AddUser(SecurityLevel.Administrator);
        }

        public AppDbContext Db() => _factory.CreateDbContext();

        public User AddUser(SecurityLevel rank)
        {
            var id = Interlocked.Increment(ref _accountIds);
            var user = new User { Id = Guid.NewGuid(), AccountId = id, Username = $"user{id}", PasswordHash = string.Empty, Rank = rank };
            using var db = Db();
            db.Users.Add(user);
            db.SaveChanges();
            return user;
        }

        public ModerationIssue AddIssue(User reporter, User? reported, User? pickedBy = null)
        {
            var issue = new ModerationIssue
            {
                ReporterId = reporter.Id,
                ReportedUserId = reported?.Id,
                State = pickedBy is null ? ModerationIssueState.Open : ModerationIssueState.Picked,
                PickerId = pickedBy?.Id,
                Message = "help",
            };
            using var db = Db();
            db.ModerationIssues.Add(issue);
            db.SaveChanges();
            return issue;
        }

        public Room AddRoom()
        {
            var room = new Room { Name = "a room", OwnerId = Moderator.Id };
            using var db = Db();
            db.Rooms.Add(room);
            db.SaveChanges();
            return room;
        }
    }

    private sealed class Factory(string name) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;

        public AppDbContext CreateDbContext() => new(_options);
    }
}

/// <summary>One call a grain was asked: of which grain (its interface and key), what, and with what.</summary>
public sealed record GrainCall(Type Grain, object? Key, string Method, object?[] Args);

/// <summary>Grains that do nothing but note what they were asked, answering with nothing (an empty list, no room).</summary>
public class FakeGrains : DispatchProxy
{
    private ConcurrentQueue<GrainCall> _calls = new();
    private Type _grain = typeof(IGrainFactory);
    private object? _key;

    public IGrainFactory Factory { get; private set; } = null!;

    public static FakeGrains Create()
    {
        var factory = DispatchProxy.Create<IGrainFactory, FakeGrains>();
        var fake = (FakeGrains)(object)factory;
        fake.Factory = factory;
        return fake;
    }

    public IEnumerable<GrainCall> Calls(string method) => _calls.Where(c => c.Method == method);

    /// <summary>The headers of what was sent to a player.</summary>
    public IEnumerable<ushort> SentTo(Guid playerId) => _calls
        .Where(c => c.Grain == typeof(IPlayerGrain) && Equals(c.Key, playerId) && c.Method == nameof(IPlayerGrain.SendAsync))
        .Select(c => ((ComposedMessage)c.Args[0]!).Header);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        args ??= [];
        if (_grain == typeof(IGrainFactory))
        {
            if (!method.Name.StartsWith(nameof(IGrainFactory.GetGrain), StringComparison.Ordinal) || !method.ReturnType.IsInterface)
                throw new NotSupportedException(method.Name);

            var grain = (FakeGrains)DispatchProxy.Create(method.ReturnType, typeof(FakeGrains));
            (grain._calls, grain._grain, grain._key) = (_calls, method.ReturnType, args.Length > 0 ? args[0] : null);
            return grain;
        }

        _calls.Enqueue(new GrainCall(_grain, _key, method.Name, args));
        return Answer(method.ReturnType);
    }

    private static object? Answer(Type type)
    {
        if (type == typeof(Task))
            return Task.CompletedTask;
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Task<>))
            return null;

        var result = type.GetGenericArguments()[0];
        object? value = result.IsValueType ? Activator.CreateInstance(result)
            : result.IsGenericType && result.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? Array.CreateInstance(result.GetGenericArguments()[0], 0)
            : result.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(result)
            : null;
        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
    }
}
