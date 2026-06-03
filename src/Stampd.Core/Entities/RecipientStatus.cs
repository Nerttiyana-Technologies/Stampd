namespace Stampd.Core.Entities;

/// <summary>
/// Lifecycle states of a <see cref="Recipient"/> within a signing request.
/// </summary>
public enum RecipientStatus
{
    /// <summary>Not yet eligible to sign (earlier routing slot still pending).</summary>
    Pending = 0,

    /// <summary>Invitation dispatched; awaiting first open.</summary>
    Invited = 1,

    /// <summary>Recipient opened the signing page.</summary>
    Viewed = 2,

    /// <summary>Recipient applied all required signatures.</summary>
    Signed = 3,

    /// <summary>Recipient declined to sign.</summary>
    Declined = 4,

    /// <summary>Recipient's window expired without action.</summary>
    Expired = 5,
}
