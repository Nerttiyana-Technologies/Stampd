namespace Stampd.Core.Entities;

/// <summary>
/// A role definition on a <see cref="DocumentTemplate"/>. Roles are slots filled by
/// concrete <see cref="Recipient"/> instances when the template is dispatched.
/// </summary>
/// <remarks>
/// Roles carry the routing order so the same template can express sequential, parallel,
/// or hybrid signing workflows without hardcoding email addresses at design time.
/// </remarks>
public sealed class TemplateRecipientRole
{
    public Guid Id { get; set; }

    public Guid DocumentTemplateId { get; set; }
    public DocumentTemplate? DocumentTemplate { get; set; }

    /// <summary>Human-readable role name shown in the designer (e.g. "Customer", "Approver").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 1-based routing order. Roles with the same number sign in parallel; roles with
    /// higher numbers are blocked until lower numbers complete.
    /// </summary>
    public int RoutingOrder { get; set; }

    /// <summary>If true, the recipient assigned to this role must complete identity verification before signing.</summary>
    public bool RequiresIdentityVerification { get; set; }

    public ICollection<TemplateField> Fields { get; set; } = new List<TemplateField>();
}
