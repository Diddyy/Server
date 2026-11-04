using System.Net.Mail;
using Habbo.Domain.Users;
using Habbo.Infrastructure;
using Habbo.Protocol.Messages.Outgoing.Avatar;
using Habbo.Protocol.Messages.Outgoing.Handshake;
using Habbo.Protocol.Messages.Outgoing.Inventory.Purse;
using Habbo.Protocol.Messages.Outgoing.Moderation;
using Habbo.Protocol.Messages.Outgoing.Notifications;
using Habbo.Protocol.Messages.Outgoing.Perk;
using Habbo.Silo.Abstractions.Achievements;
using Habbo.Silo.Abstractions.Players;
using Habbo.Silo.Abstractions.Rooms;
using Habbo.Silo.Users;
using Habbo.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Habbo.Host;

/// <summary>The CMS bridge (/api/cms/*): what a website that edits the database itself can't do on its own - make an
/// online player see the change, alert or disconnect them, alert the hotel, count who is online - and registering a
/// player with the hotel's own rules. Every call is signed with the shared secret (<see cref="CmsBridgeSignature"/>);
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
        cms.MapPost("/players", CreatePlayerAsync);
        cms.MapPost("/players/{id:guid}/reload", ReloadAsync);
        cms.MapPost("/players/{id:guid}/alert", AlertAsync);
        cms.MapPost("/players/{id:guid}/disconnect", DisconnectAsync);
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
        Results.Ok(new { online = await Presence(grains).WhichAreOnlineAsync((request.Ids ?? []).Distinct().Take(1000).ToList()) });

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

    /// <summary>The CMS changed the player in the database (credits, rank, motto, look): the cached player is read
    /// again and, when they are online, they see it at once - as :giverank does.</summary>
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

    private static IUserPresenceGrain Presence(IGrainFactory grains) =>
        grains.GetGrain<IUserPresenceGrain>(IUserPresenceGrain.Key);

    private static async Task<bool> IsOnlineAsync(IGrainFactory grains, Guid id) =>
        (await Presence(grains).WhichAreOnlineAsync([id])).Count > 0;

    private static string? Message(MessageRequest request, CmsBridgeOptions options)
    {
        var message = request.Message?.Trim() ?? string.Empty;
        return message.Length == 0 ? null : message.Length > options.MessageMaxLength ? message[..options.MessageMaxLength] : message;
    }

    private static IResult Refused(string error) => Results.BadRequest(new { error });

    public sealed record OnlineRequest(List<Guid>? Ids);

    public sealed record MessageRequest(string? Message);

    public sealed record DisconnectRequest(bool Banned, string? Message);

    public sealed record CreatePlayerRequest(
        string? Username, string? Email, string? Password, string? Figure, string? Gender, string? Motto,
        int? Credits, int? Rank, string? InviteCode);
}
