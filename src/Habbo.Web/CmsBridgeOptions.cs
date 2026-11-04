namespace Habbo.Web;

/// <summary>The CMS bridge: a signed API a website (such as RevCMS) uses to act on the running hotel - reload a
/// player it changed in the database, alert or disconnect them, alert the hotel, count who is online.</summary>
public sealed class CmsBridgeOptions
{
    public const string Section = "CmsBridge";

    /// <summary>The secret the website signs its calls with (see <see cref="CmsBridgeSignature"/>). Empty turns the
    /// bridge off: its routes aren't mapped at all.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>How far a call's signing time may be from the hotel's clock before it is refused.</summary>
    public int MaxClockSkewSeconds { get; set; } = 300;

    /// <summary>The longest alert a call may send.</summary>
    public int MessageMaxLength { get; set; } = 1000;
}
