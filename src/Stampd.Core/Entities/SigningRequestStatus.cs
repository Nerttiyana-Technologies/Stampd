namespace Stampd.Core.Entities;

/// <summary>
/// Lifecycle states of a <see cref="SigningRequest"/>.
/// </summary>
public enum SigningRequestStatus
{
    /// <summary>Created by the sender but not yet dispatched.</summary>
    Draft = 0,

    /// <summary>Dispatched to recipients; awaiting the first signer.</summary>
    Sent = 1,

    /// <summary>At least one recipient has acted but not all have completed.</summary>
    InProgress = 2,

    /// <summary>All required recipients have signed and the document is sealed.</summary>
    Completed = 3,

    /// <summary>A recipient declined; the workflow is terminated.</summary>
    Declined = 4,

    /// <summary>The configured expiry passed without completion.</summary>
    Expired = 5,

    /// <summary>The sender voided the request after dispatch.</summary>
    Voided = 6,
}
