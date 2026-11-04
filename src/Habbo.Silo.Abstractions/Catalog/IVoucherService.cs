using Habbo.Silo.Abstractions.Players;

namespace Habbo.Silo.Abstractions.Catalog;

/// <summary>Voucher codes redeemed from the catalog (or the website, through the CMS bridge).</summary>
public interface IVoucherService
{
    /// <summary>Redeems <paramref name="code"/> for the player and tells their client either way.</summary>
    Task<VoucherRedeemResult> RedeemAsync(PlayerData player, string code);
}

/// <summary>What a redeem gave: nothing when <paramref name="Redeemed"/> is false (a wrong, used-up or expired code,
/// one this player already redeemed, or too many wrong codes lately). <paramref name="Offer"/> is the offer's
/// localization id when its furni were handed over.</summary>
public sealed record VoucherRedeemResult(bool Redeemed, int Credits = 0, int Points = 0, int PointType = 0, string? Offer = null)
{
    public static readonly VoucherRedeemResult Refused = new(false);
}
