using System.Net.Mail;
using Habbo.Domain.Moderation;
using Habbo.Domain.Users;
using Habbo.Infrastructure;
using Habbo.Protocol.Messages.Outgoing.Avatar;
using Habbo.Protocol.Messages.Outgoing.Handshake;
using Habbo.Protocol.Messages.Outgoing.Inventory.Purse;
using Habbo.Protocol.Messages.Outgoing.Moderation;
using Habbo.Protocol.Messages.Outgoing.Notifications;
using Habbo.Protocol.Messages.Outgoing.Perk;
using Habbo.Silo.Abstractions.Achievements;
using Habbo.Silo.Abstractions.Moderation;
using Habbo.Silo.Abstractions.Navigator;
using Habbo.Silo.Abstractions.Players;
using Habbo.Silo.Abstractions.Rooms;
using Habbo.Silo.Users;
using Habbo.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Habbo.Host;

/// <summary>The CMS bridge (/api/cms/*): what a website that edits the database itself can't do on its own - make an
/// online player see the change, alert or disconnect them, alert the hotel, count who is online and where - registering
/// a player with the hotel's own rules, and the staff panel's moderation, which goes through the mod tool's own service
/// (its permissions, ranks and effects) for the staff member named. Every call is signed with the shared secret (<see cref="CmsBridgeSignature"/>);
/// the routes only exist when a secret is set and this process runs the game.</summary>
public static class CmsBridgeEndpoints
{
    private const string Prefix = "/api/cms";

    public static bool IsEnabled(IConfiguration configuration) =>
        !string.IsNullOrEmpty(configuration.GetSection(CmsBridgeOptions.Section).Get<CmsBridgeOptions>()?.Secret);

    public static void MapCmsBridgeEndpoints(this WebApplication app)
    {
        // the signature covers the body, so it is checked before the endpoint reads it
        app.UseWhen(context => context.Request.Path.StartsWithSegments(Prefix), branch => branch.Use(VerifySignatureAsync));

        var cms = app.MapGroup(Prefix);
        cms.MapGet("/stats/online", OnlineCountAsync);
        cms.MapPost("/players/online", WhichAreOnlineAsync);
        cms.MapGet("/players/online/where", WhereAreOnlineAsync);
        cms.MapPost("/players", CreatePlayerAsync);
        cms.MapPost("/players/{id:guid}/reload", ReloadAsync);
        cms.MapPost("/players/{id:guid}/alert", AlertAsync);
        cms.MapPost("/players/{id:guid}/disconnect", DisconnectAsync);
        cms.MapPost("/players/{id:guid}/sanction", SanctionAsync);
        cms.MapPost("/players/{id:guid}/sanction/next", NextSanctionAsync);
        cms.MapPost("/issues/pick", PickIssuesAsync);
        cms.MapPost("/issues/release", ReleaseIssuesAsync);
        cms.MapPost("/issues/close", CloseIssuesAsync);
        cms.MapPost("/issues/{id:int}/resolve", ResolveIssueAsync);
        cms.MapGet("/rooms/{id:int}/occupants", OccupantsAsync);
        cms.MapPost("/rooms/{id:int}/moderate", ModerateRoomAsync);
        cms.MapPost("/rooms/{id:int}/message", RoomMessageAsync);
        cms.MapPost("/hotel/alert", HotelAlertAsync);
    }

    private static async Task VerifySignatureAsync(HttpContext context, Func<Task> next)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<CmsBridgeOptions>>().Value;
        context.Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
            body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;

        var request = context.Request;
        if (!CmsBridgeSignature.Verify(
                options.Secret,
                request.Headers[CmsBridgeSignature.TimestampHeader],
                request.Headers[CmsBridgeSignature.SignatureHeader],
                request.Method,
                $"{request.PathBase}{request.Path}{request.QueryString}",
                body,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(options.MaxClockSkewSeconds)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next();
    }

    private static async Task<IResult> OnlineCountAsync(IGrainFactory grains) =>
        Results.Ok(new { online = await Presence(grains).CountAsync() });

    private static async Task<IResult> WhichAreOnlineAsync(OnlineRequest request, IGrainFactory grains) =>
        Results.Ok(new { online = await Presence(grains).WhichAreOnlineAsync((request.Ids ?? []).Distinct().Take(MaxPlayers).ToList()) });

    /// <summary>Everyone online (as many as <see cref="MaxPlayers"/>) and the room each is in, if any.</summary>
    private static async Task<IResult> WhereAreOnlineAsync(IGrainFactory grains)
    {
        var online = (await Presence(grains).GetOnlineAsync()).Take(MaxPlayers).ToList();
        var rooms = await Task.WhenAll(online.Select(id => grains.GetGrain<IPlayerGrain>(id).GetCurrentRoomAsync()));
        return Results.Ok(new { players = online.Select((id, i) => new { id, roomId = rooms[i] }) });
    }

    /// <summary>The most players a call asks about, or lists.</summary>
    private const int MaxPlayers = 1000;

    /// <summary>Registers a player as the website's register endpoint does (the name rules, the password hash, the
    /// invite code taken in the same transaction), with the look and starting values the CMS chose.</summary>
    private static async Task<IResult> CreatePlayerAsync(
        CreatePlayerRequest request,
        IDbContextFactory<AppDbContext> dbFactory,
        IPasswordHasher<User> passwordHasher,
        INameRules nameRules,
        IOptions<AuthOptions> options,
        CancellationToken ct)
    {
        var rules = options.Value;
        var username = request.Username?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        var code = request.InviteCode?.Trim().ToUpperInvariant();

        if (password.Length < rules.PasswordMinLength || password.Length > rules.PasswordMaxLength)
            return Refused(RegisterError.Password);
        if (email.Length > User.EmailMaxLength || (email.Length > 0 && !MailAddress.TryCreate(email, out _)))
            return Refused(RegisterError.Email);
        var verdict = await nameRules.JudgeAsync(username);
        if (verdict != NameVerdict.Available)
            return Refused(verdict switch
            {
                NameVerdict.TooShort => RegisterError.NameTooShort,
                NameVerdict.TooLong => RegisterError.NameTooLong,
                NameVerdict.Taken => RegisterError.NameTaken,
                _ => RegisterError.NameInvalid,
            });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = new User
        {
            Username = username,
            PasswordHash = string.Empty,
            Email = email,
            Credits = request.Credits ?? rules.StartingCredits,
            Motto = request.Motto?.Trim() ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(request.Figure))
            user.Figure = request.Figure.Trim();
        if (!string.IsNullOrEmpty(request.Gender))
            user.Gender = GenderExtensions.ParseLetter(request.Gender);
        if (request.Rank is { } rank && Enum.IsDefined((SecurityLevel)rank))
            user.Rank = (SecurityLevel)rank;
        user.PasswordHash = passwordHasher.HashPassword(user, password);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // registered by someone else between the check and now
            return Refused(RegisterError.NameTaken);
        }

        // an invite is only needed when the CMS sends one: the CMS decides whether its hotel is invite-only
        if (!string.IsNullOrEmpty(code) && await db.InviteCodes.Where(i => i.Code == code && i.UsedById == null)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedById, user.Id).SetProperty(i => i.UsedAt, DateTimeOffset.UtcNow), ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return Refused(RegisterError.InviteCode);
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(new { id = user.Id, accountId = user.AccountId });
    }

    /// <summary>The CMS changed the player in the database (credits, duckets and the other currencies, rank, motto,
    /// look): the cached player is read again and, when they are online, they see it at once - as :giverank does.</summary>
    private static async Task<IResult> ReloadAsync(Guid id, IGrainFactory grains, PlayerMessenger messenger, IPerkService perks)
    {
        var player = grains.GetGrain<IPlayerGrain>(id);
        var data = await player.ReloadAsync();
        var online = await IsOnlineAsync(grains, id);
        if (online)
        {
            await messenger.SendAsync(id, UserRightsMessageComposer.From(data));
            await messenger.SendAsync(id, PerkAllowancesMessageComposer.From(await perks.GetAllowancesAsync(data)));
            await messenger.SendAsync(id, new CreditBalanceComposer(data.Credits));
            await player.SendActivityPointsAsync();
            await messenger.SendAsync(id, new FigureUpdateComposer(data.Figure, data.Gender.ToLetter()));
            if (await player.GetCurrentRoomAsync() is { } roomId)
                await grains.GetGrain<IGameRoomGrain>(roomId).RefreshPlayerAsync(id, data);
        }

        return Results.Ok(new { online });
    }

    private static async Task<IResult> AlertAsync(
        Guid id, MessageRequest request, IGrainFactory grains, PlayerMessenger messenger, IOptions<CmsBridgeOptions> options)
    {
        if (Message(request, options.Value) is not { } message)
            return Results.BadRequest(new { error = "message" });
        if (!await IsOnlineAsync(grains, id))
            return Results.Ok(new { online = false });

        await messenger.SendAsync(id, new ModeratorMessageComposer(message, string.Empty));
        return Results.Ok(new { online = true });
    }

    /// <summary>Signs a player out of the hotel. A ban is told as one (and they leave their room first), as the
    /// moderation tool does.</summary>
    private static async Task<IResult> DisconnectAsync(
        Guid id, DisconnectRequest request, IGrainFactory grains, PlayerMessenger messenger, IOptions<CmsBridgeOptions> options)
    {
        if (!await IsOnlineAsync(grains, id))
            return Results.Ok(new { online = false });

        var player = grains.GetGrain<IPlayerGrain>(id);
        if (request.Banned)
        {
            if (await player.GetCurrentRoomAsync() is { } roomId)
                await grains.GetGrain<IGameRoomGrain>(roomId).StaffKickAsync(id);
            var message = Message(new MessageRequest(request.Message), options.Value) ?? string.Empty;
            await messenger.SendAsync(id, new UserBannedMessageComposer(message));
        }

        await player.DisconnectAsync(request.Banned ? DisconnectReasonComposer.JustBanned : DisconnectReasonComposer.Logout);
        return Results.Ok(new { online = true });
    }

    private static async Task<IResult> HotelAlertAsync(
        MessageRequest request, IGrainFactory grains, PlayerMessenger messenger, IOptions<CmsBridgeOptions> options)
    {
        if (Message(request, options.Value) is not { } message)
            return Results.BadRequest(new { error = "message" });

        var online = await Presence(grains).GetOnlineAsync();
        await messenger.SendAsync(online, new HabboBroadcastMessageComposer(message));
        return Results.Ok(new { sent = online.Count });
    }

    /// <summary>A caution, message, mute, kick, trade lock or ban for a player, from a staff member: as the mod tool
    /// gives it, so refused as it would be there (permission, rank, no such ban).</summary>
    private static async Task<IResult> SanctionAsync(
        Guid id, SanctionRequest request, IModerationService moderation, IDbContextFactory<AppDbContext> dbFactory,
        IOptions<CmsBridgeOptions> options, CancellationToken ct)
    {
        if (KindOf(request.Kind) is not { } kind)
            return Results.BadRequest(new { error = "kind" });
        var message = Text(request.Message, options.Value);
        // a caution or a message is nothing but what it says
        if (message.Length == 0 && kind is SanctionKind.Alert or SanctionKind.Message)
            return Results.BadRequest(new { error = "message" });
        if (await AccountIdOfAsync(dbFactory, id, ct) is not { } accountId)
            return Outcome(ModerationOutcome.NotFound);

        return Outcome(await moderation.SanctionAsync(request.ModeratorId, new ModerationAction(
            kind, accountId, message, request.TopicId ?? 0, request.IssueId, request.SanctionTypeId, request.Minutes,
            request.AvatarOnly ?? false)));
    }

    /// <summary>The next step of the sanction ladder for a player, and which step that was.</summary>
    private static async Task<IResult> NextSanctionAsync(
        Guid id, NextSanctionRequest request, IModerationService moderation, IDbContextFactory<AppDbContext> dbFactory,
        IOptions<CmsBridgeOptions> options, CancellationToken ct)
    {
        var given = await AccountIdOfAsync(dbFactory, id, ct) is { } accountId
            ? await moderation.DefaultSanctionAsync(
                request.ModeratorId, accountId, request.TopicId ?? 0, Text(request.Message, options.Value), request.IssueId)
            : new LadderSanction(ModerationOutcome.NotFound);
        var kind = given.Kind is { } step ? NameOf(step) : null;
        return given.Outcome == ModerationOutcome.Done
            ? Results.Ok(new { ok = true, kind, sanctionTypeId = given.SanctionTypeId })
            : Results.Ok(new { ok = false, kind, sanctionTypeId = given.SanctionTypeId, error = ErrorOf(given.Outcome) });
    }

    /// <summary>Picks issues for a staff member: none when another moderator has any of them, who are named.</summary>
    private static async Task<IResult> PickIssuesAsync(IssuesRequest request, IModerationService moderation)
    {
        var result = await moderation.PickIssuesAsync(request.ModeratorId, request.Ids ?? [], false, 0, string.Empty);
        var taken = result.Taken.Select(t => new { id = t.IssueId, by = t.PickerName });
        return result.Outcome == ModerationOutcome.Done
            ? Results.Ok(new { picked = result.Picked, taken })
            : Results.Ok(new { picked = result.Picked, taken, error = ErrorOf(result.Outcome) });
    }

    private static async Task<IResult> ReleaseIssuesAsync(IssuesRequest request, IModerationService moderation) =>
        Results.Ok(new { released = await moderation.ReleaseIssuesAsync(request.ModeratorId, request.Ids ?? []) });

    /// <summary>Closes the issues the staff member picked, for a reason (useless, abusive, resolved).</summary>
    private static async Task<IResult> CloseIssuesAsync(CloseIssuesRequest request, IModerationService moderation)
    {
        if (!Enum.IsDefined((ModerationCloseReason)request.Reason))
            return Results.BadRequest(new { error = "reason" });

        var result = await moderation.CloseIssuesAsync(request.ModeratorId, request.Ids ?? [], request.Reason);
        return result.Outcome == ModerationOutcome.Done
            ? Results.Ok(new { closed = result.Closed })
            : Results.Ok(new { closed = result.Closed, error = ErrorOf(result.Outcome) });
    }

    /// <summary>Resolves an issue the staff member picked (and others with it), giving its reported user the next
    /// step of the ladder; left open when that is refused.</summary>
    private static async Task<IResult> ResolveIssueAsync(int id, ResolveIssueRequest request, IModerationService moderation) =>
        Outcome(await moderation.CloseWithDefaultSanctionAsync(request.ModeratorId, id, request.OtherIds ?? [], request.TopicId));

    /// <summary>Who is in a room now; nobody when it isn't loaded, which asking it would do.</summary>
    private static async Task<IResult> OccupantsAsync(int id, IGrainFactory grains)
    {
        var population = await grains.GetGrain<IRoomPopulationGrain>(0).GetAsync();
        IReadOnlyList<Guid> players = population.GetValueOrDefault(id) > 0
            ? await grains.GetGrain<IGameRoomGrain>(id).GetUserIdsAsync()
            : [];
        return Results.Ok(new { players });
    }

    private static async Task<IResult> ModerateRoomAsync(int id, ModerateRoomRequest request, IModerationService moderation) =>
        Outcome(await moderation.ModerateRoomAsync(request.ModeratorId, id, request.LockDoor, request.ChangeTitle, request.KickUsers));

    /// <summary>A message (or caution) from a staff member to everyone in a room but staff, who may be sent out too.</summary>
    private static async Task<IResult> RoomMessageAsync(
        int id, RoomMessageRequest request, IModerationService moderation, IOptions<CmsBridgeOptions> options)
    {
        if (Message(new MessageRequest(request.Message), options.Value) is not { } message)
            return Results.BadRequest(new { error = "message" });

        return Outcome(await moderation.RoomMessageAsync(request.ModeratorId, id, message, request.Caution, request.Kick));
    }

    private static async Task<int?> AccountIdOfAsync(IDbContextFactory<AppDbContext> dbFactory, Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Users.Where(u => u.Id == id).Select(u => (int?)u.AccountId).FirstOrDefaultAsync(ct);
    }

    /// <summary>The sanctions by the names the CMS sends.</summary>
    private static readonly Dictionary<string, SanctionKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alert"] = SanctionKind.Alert,
        ["mute"] = SanctionKind.Mute,
        ["ban"] = SanctionKind.Ban,
        ["kick"] = SanctionKind.Kick,
        ["trade_lock"] = SanctionKind.TradeLock,
        ["message"] = SanctionKind.Message,
    };

    private static SanctionKind? KindOf(string? name) =>
        name is not null && Kinds.TryGetValue(name, out var kind) ? kind : null;

    private static string NameOf(SanctionKind kind) => Kinds.First(k => k.Value == kind).Key;

    private static IResult Outcome(ModerationOutcome outcome) =>
        outcome == ModerationOutcome.Done ? Results.Ok(new { ok = true }) : Results.Ok(new { ok = false, error = ErrorOf(outcome) });

    private static string ErrorOf(ModerationOutcome outcome) => outcome switch
    {
        ModerationOutcome.NotFound => "not_found",
        ModerationOutcome.Permission => "permission",
        ModerationOutcome.Rank => "rank",
        ModerationOutcome.Type => "type",
        _ => string.Empty,
    };

    private static IUserPresenceGrain Presence(IGrainFactory grains) =>
        grains.GetGrain<IUserPresenceGrain>(IUserPresenceGrain.Key);

    private static async Task<bool> IsOnlineAsync(IGrainFactory grains, Guid id) =>
        (await Presence(grains).WhichAreOnlineAsync([id])).Count > 0;

    private static string? Message(MessageRequest request, CmsBridgeOptions options) =>
        Text(request.Message, options) is { Length: > 0 } message ? message : null;

    /// <summary>What a call says, trimmed and cut to the longest allowed; empty when it says nothing.</summary>
    private static string Text(string? text, CmsBridgeOptions options)
    {
        var message = text?.Trim() ?? string.Empty;
        return message.Length > options.MessageMaxLength ? message[..options.MessageMaxLength] : message;
    }

    private static IResult Refused(string error) => Results.BadRequest(new { error });

    public sealed record OnlineRequest(List<Guid>? Ids);

    public sealed record MessageRequest(string? Message);

    public sealed record DisconnectRequest(bool Banned, string? Message);

    /// <summary>A sanction from <paramref name="ModeratorId"/> (the staff member's user id): <paramref name="Kind"/>
    /// is alert, mute, kick, trade_lock, message or ban; a ban needs the ladder's <paramref name="SanctionTypeId"/>,
    /// a trade lock its <paramref name="Minutes"/>.</summary>
    public sealed record SanctionRequest(
        Guid ModeratorId, string? Kind, string? Message, int? Minutes, int? SanctionTypeId, bool? AvatarOnly,
        int? IssueId, int? TopicId);

    public sealed record NextSanctionRequest(Guid ModeratorId, string? Message, int? TopicId, int? IssueId);

    public sealed record IssuesRequest(Guid ModeratorId, List<int>? Ids);

    /// <summary><paramref name="Reason"/>: 1 useless, 2 abusive, 3 resolved.</summary>
    public sealed record CloseIssuesRequest(Guid ModeratorId, List<int>? Ids, int Reason);

    public sealed record ResolveIssueRequest(Guid ModeratorId, int TopicId, List<int>? OtherIds);

    public sealed record ModerateRoomRequest(Guid ModeratorId, bool LockDoor, bool ChangeTitle, bool KickUsers);

    public sealed record RoomMessageRequest(Guid ModeratorId, string? Message, bool Caution, bool Kick);

    public sealed record CreatePlayerRequest(
        string? Username, string? Email, string? Password, string? Figure, string? Gender, string? Motto,
        int? Credits, int? Rank, string? InviteCode);
}
