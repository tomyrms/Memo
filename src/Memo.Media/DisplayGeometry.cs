using Memo.Core;
using SkiaSharp;

namespace Memo.Media;

/// <summary>Dimensions dans le sens d'affichage, distinctes des dimensions encodées.</summary>
public readonly record struct DisplayDimensions(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public float AspectRatio => IsEmpty ? 0f : Width / (float)Height;

    public SKSize ToSkSize() => IsEmpty ? new SKSize(0, 0) : new SKSize(Width, Height);
}

/// <summary>
/// Géométrie d'affichage d'un média.
///
/// Rotation des images : <see cref="MediaInfo.Width"/> et <see cref="MediaInfo.Height"/> sont les
/// dimensions encodées ; <see cref="MediaInfo.Rotation"/> (0, 90, 180 ou 270) est appliquée ici.
/// <see cref="ImageDecoder"/> applique déjà cette rotation aux pixels : si l'on dispose de son
/// résultat, utiliser directement ses dimensions d'affichage plutôt que de réappliquer la rotation.
///
/// Rotation des vidéos : LibVLC/libvlcsharp applique déjà la rotation portée par les métadonnées
/// vidéo avant de livrer les frames. Pour une vidéo, les dimensions doivent donc être celles des
/// frames décodées (ou <see cref="MediaInfo.Rotation"/> doit valoir 0). L'interface ne doit jamais
/// rappeler la rotation du conteneur sur une frame déjà tournée ; le paramètre
/// <c>dimensionsAreDecoderOutput</c> exprime ce contrat pour les appelants qui stockent les
/// dimensions de sortie du décodeur.
/// </summary>
public static class DisplayGeometry
{
    /// <summary>
    /// Calcule les dimensions affichées à partir des dimensions encodées et d'une rotation en degrés.
    /// Une rotation négative est normalisée (par exemple -90 devient 270).
    /// </summary>
    public static DisplayDimensions GetDisplaySize(int encodedWidth, int encodedHeight, int rotation)
    {
        if (encodedWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodedWidth),
                encodedWidth,
                "La largeur encodée doit être strictement positive.");
        }

        if (encodedHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodedHeight),
                encodedHeight,
                "La hauteur encodée doit être strictement positive.");
        }

        int normalized = NormalizeRotation(rotation);
        return normalized is 90 or 270
            ? new DisplayDimensions(encodedHeight, encodedWidth)
            : new DisplayDimensions(encodedWidth, encodedHeight);
    }

    /// <summary>
    /// Dimensions affichées d'un média. Retourne des dimensions vides lorsque le média n'a pas de
    /// dimensions connues. Pour une vidéo dont les pixels ont déjà été tournés par le décodeur,
    /// passer <paramref name="dimensionsAreDecoderOutput"/> à <c>true</c> : la rotation de
    /// <see cref="MediaInfo.Rotation"/> est alors ignorée.
    /// </summary>
    public static DisplayDimensions GetDisplaySize(
        MediaInfo media,
        bool dimensionsAreDecoderOutput = false)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.Width is not > 0 || media.Height is not > 0)
        {
            return new DisplayDimensions(0, 0);
        }

        if (dimensionsAreDecoderOutput)
        {
            return new DisplayDimensions(media.Width.Value, media.Height.Value);
        }

        return GetDisplaySize(media.Width.Value, media.Height.Value, media.Rotation);
    }

    /// <summary>
    /// Rectangle « Uniform » (letterbox) qui contient le contenu entier dans la zone cible, sans
    /// déformation, centré horizontalement et verticalement. Retourne un rectangle vide si le
    /// contenu ou la cible n'a pas de dimensions exploitables.
    /// </summary>
    public static SKRect GetUniformRect(DisplayDimensions content, SKSize target)
    {
        if (content.IsEmpty || target.Width <= 0 || target.Height <= 0)
        {
            return new SKRect(0, 0, 0, 0);
        }

        float scale = Math.Min(
            target.Width / content.Width,
            target.Height / content.Height);
        float width = content.Width * scale;
        float height = content.Height * scale;
        float left = (target.Width - width) / 2f;
        float top = (target.Height - height) / 2f;
        return new SKRect(left, top, left + width, top + height);
    }

    /// <summary>Rectangle « Uniform » calculé depuis un <see cref="MediaInfo"/>.</summary>
    public static SKRect GetUniformRect(
        MediaInfo media,
        SKSize target,
        bool dimensionsAreDecoderOutput = false) =>
        GetUniformRect(GetDisplaySize(media, dimensionsAreDecoderOutput), target);

    private static int NormalizeRotation(int rotation)
    {
        int normalized = rotation % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        if (normalized % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rotation),
                rotation,
                "La rotation doit être un multiple de 90 degrés (0, 90, 180 ou 270).");
        }

        return normalized;
    }
}
