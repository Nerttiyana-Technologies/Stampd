namespace Stampd.Core.Entities;

/// <summary>
/// A single field placement on a <see cref="DocumentTemplate"/>. Coordinates are stored
/// as percentages so the field stays aligned across desktop, mobile, and print displays.
/// </summary>
public sealed class TemplateField
{
    public Guid Id { get; set; }

    public Guid DocumentTemplateId { get; set; }
    public DocumentTemplate? DocumentTemplate { get; set; }

    /// <summary>Role that fills this field. Null for pre-filled fields (e.g. company logo, date stamp).</summary>
    public Guid? AssignedRoleId { get; set; }
    public TemplateRecipientRole? AssignedRole { get; set; }

    /// <summary>1-based page index where the field appears.</summary>
    public int PageNumber { get; set; }

    /// <summary>Percentage X coordinate of the field's top-left corner, [0, 100].</summary>
    public double BoundsX { get; set; }

    /// <summary>Percentage Y coordinate of the field's top-left corner, [0, 100].</summary>
    public double BoundsY { get; set; }

    /// <summary>Percentage width of the field, [0, 100].</summary>
    public double BoundsWidth { get; set; }

    /// <summary>Percentage height of the field, [0, 100].</summary>
    public double BoundsHeight { get; set; }

    /// <summary>The kind of input this field captures.</summary>
    public SignatureFieldKind Kind { get; set; }

    /// <summary>True if the recipient must complete this field before signing.</summary>
    public bool IsRequired { get; set; } = true;

    /// <summary>Optional label rendered next to the field in the signing UI.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Default value for pre-filled fields (e.g. a date stamp populated at send time). UTF-8
    /// encoded; ignored for fields the recipient is expected to fill.
    /// </summary>
    public string? DefaultValue { get; set; }
}
