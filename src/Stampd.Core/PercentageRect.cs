namespace Stampd.Core;

/// <summary>
/// A rectangle on a PDF page expressed in percentage coordinates relative to the page's
/// bounding box. All values are in the closed interval [0.0, 100.0].
/// </summary>
/// <remarks>
/// Percentage coordinates are used throughout Stampd in preference to absolute pixels so
/// that field positions remain stable across desktop, mobile, and print displays. The PDF
/// engine resolves percentages against the page's MediaBox at render time.
/// </remarks>
public readonly record struct PercentageRect(double X, double Y, double Width, double Height)
{
    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> if any component is outside [0, 100]
    /// or if the rectangle extends past the page edge.
    /// </summary>
    public void EnsureValid()
    {
        EnsureInRange(X, nameof(X));
        EnsureInRange(Y, nameof(Y));
        EnsureInRange(Width, nameof(Width));
        EnsureInRange(Height, nameof(Height));

        if (X + Width > 100.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Width),
                $"X ({X}) + Width ({Width}) exceeds 100.");
        }

        if (Y + Height > 100.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Height),
                $"Y ({Y}) + Height ({Height}) exceeds 100.");
        }
    }

    private static void EnsureInRange(double value, string name)
    {
        if (value is < 0.0 or > 100.0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be in [0, 100].");
        }
    }
}
