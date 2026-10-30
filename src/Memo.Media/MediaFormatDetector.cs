using System.Buffers.Binary;
using Memo.Core;

namespace Memo.Media;

/// <summary>Familles de formats reconnues par examen des octets, jamais par le nom du fichier.</summary>
public enum MediaFormatKind
{
    Unknown = 0,
    Jpeg = 1,
    Png = 2,
    WebP = 3,
    Heic = 4,
    Mp4 = 5,
    Mov = 6,
}

/// <summary>Format binaire réellement identifié et extension cohérente avec ce contenu.</summary>
public sealed record MediaFormatInfo(
    MediaFormatKind Format,
    string Name,
    string Extension,
    MediaKind MediaKind,
    string MimeType)
{
    public bool IsKnown => Format != MediaFormatKind.Unknown;
    public bool IsImage => MediaKind == Memo.Core.MediaKind.Photo;
    public bool IsVideo => MediaKind == Memo.Core.MediaKind.Video;

    public static MediaFormatInfo Unknown { get; } =
        new(MediaFormatKind.Unknown, "inconnu", ".bin", Memo.Core.MediaKind.Unknown, "application/octet-stream");

    public static MediaFormatInfo Jpeg { get; } =
        new(MediaFormatKind.Jpeg, "jpeg", ".jpg", Memo.Core.MediaKind.Photo, "image/jpeg");

    public static MediaFormatInfo Png { get; } =
        new(MediaFormatKind.Png, "png", ".png", Memo.Core.MediaKind.Photo, "image/png");

    public static MediaFormatInfo WebP { get; } =
        new(MediaFormatKind.WebP, "webp", ".webp", Memo.Core.MediaKind.Photo, "image/webp");

    public static MediaFormatInfo Heic { get; } =
        new(MediaFormatKind.Heic, "heic", ".heic", Memo.Core.MediaKind.Photo, "image/heic");

    public static MediaFormatInfo Mp4 { get; } =
        new(MediaFormatKind.Mp4, "mp4", ".mp4", Memo.Core.MediaKind.Video, "video/mp4");

    public static MediaFormatInfo Mov { get; } =
        new(MediaFormatKind.Mov, "mov", ".mov", Memo.Core.MediaKind.Video, "video/quicktime");
}

/// <summary>
/// Détection de format par signature binaire. Un WebP nommé <c>.png</c> reste un WebP :
/// l'extension annoncée n'est qu'une provenance, jamais une preuve.
/// </summary>
public static class MediaFormatDetector
{
    /// <summary>Nombre d'octets examinés au maximum pour trouver un en-tête ISO BMFF.</summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>Détecte le format d'un fichier. Lève une exception typée si le fichier est inaccessible.</summary>
    public static MediaFormatInfo Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Le chemin du fichier est vide.", nameof(path));
        }

        var header = new byte[MaxHeaderBytes];
        int read;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.SequentialScan);
            read = ReadHeader(stream, header);
        }
        catch (FileNotFoundException ex)
        {
            throw Missing(path, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw Missing(path, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MediaDecodeException(
                $"Le fichier « {path} » ne peut pas être lu : accès refusé.",
                "fichier-acces-refuse",
                path,
                ex);
        }
        catch (IOException ex)
        {
            throw new MediaDecodeException(
                $"Le fichier « {path} » est verrouillé ou illisible.",
                "fichier-illisible",
                path,
                ex);
        }

        return Detect(header.AsSpan(0, read));
    }

    /// <summary>Détecte le format à partir des premiers octets du fichier.</summary>
    public static MediaFormatInfo Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return MediaFormatInfo.Jpeg;
        }

        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return MediaFormatInfo.Png;
        }

        if (header.Length >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return MediaFormatInfo.WebP;
        }

        return DetectIsoBmff(header);
    }

    /// <summary>Variante non levante pour les appelants qui traitent l'inconnu comme un diagnostic.</summary>
    public static bool TryDetect(ReadOnlySpan<byte> header, out MediaFormatInfo format)
    {
        format = Detect(header);
        return format.IsKnown;
    }

    /// <summary>
    /// Détecte le format depuis un flux. La position est restaurée lorsque le flux est déplaçable ;
    /// un flux non déplaçable est consommé jusqu'à <see cref="MaxHeaderBytes"/> octets au plus.
    /// </summary>
    public static MediaFormatInfo Detect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Le flux ne peut pas être lu.", nameof(stream));
        }

        var header = new byte[MaxHeaderBytes];
        long originalPosition = 0;
        bool restore = stream.CanSeek;
        if (restore)
        {
            originalPosition = stream.Position;
        }

        try
        {
            int read = ReadHeader(stream, header);
            return Detect(header.AsSpan(0, read));
        }
        finally
        {
            if (restore)
            {
                stream.Position = originalPosition;
            }
        }
    }

    private static int ReadHeader(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static MediaFormatInfo DetectIsoBmff(ReadOnlySpan<byte> header)
    {
        int offset = 0;
        while (offset + 8 <= header.Length)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(offset, 4));
            ReadOnlySpan<byte> boxType = header.Slice(offset + 4, 4);

            int boxHeaderSize = 8;
            ulong boxSize = size32;
            if (size32 == 1)
            {
                if (offset + 16 > header.Length)
                {
                    return MediaFormatInfo.Unknown;
                }

                boxSize = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(offset + 8, 8));
                boxHeaderSize = 16;
            }
            else if (size32 == 0)
            {
                boxSize = (ulong)(header.Length - offset);
            }

            if (boxSize < (ulong)boxHeaderSize)
            {
                return MediaFormatInfo.Unknown;
            }

            if (boxType.SequenceEqual("ftyp"u8))
            {
                int available = header.Length - offset - boxHeaderSize;
                int payloadLength = (int)Math.Min(boxSize - (ulong)boxHeaderSize, (ulong)available);
                if (payloadLength < 8)
                {
                    return MediaFormatInfo.Unknown;
                }

                return DetectFtyp(header.Slice(offset + boxHeaderSize, payloadLength));
            }

            if (boxSize > int.MaxValue)
            {
                return MediaFormatInfo.Unknown;
            }

            int next = offset + (int)boxSize;
            if (next <= offset || next > header.Length)
            {
                return MediaFormatInfo.Unknown;
            }

            offset = next;
        }

        return MediaFormatInfo.Unknown;
    }

    private static MediaFormatInfo DetectFtyp(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> majorBrand = payload.Slice(0, 4);

        // AVIF est un conteneur HEIF, mais ce n'est pas du HEIC et il n'est pas décodé ici.
        if (IsAvifBrand(majorBrand) || ContainsBrand(payload, "avif"u8) || ContainsBrand(payload, "avis"u8))
        {
            return MediaFormatInfo.Unknown;
        }

        if (IsHeicBrand(majorBrand) || ContainsHeicBrand(payload))
        {
            return MediaFormatInfo.Heic;
        }

        if (majorBrand.SequenceEqual("qt  "u8) || ContainsBrand(payload, "qt  "u8))
        {
            return MediaFormatInfo.Mov;
        }

        if (IsMovieBrand(majorBrand) || ContainsMovieBrand(payload))
        {
            return MediaFormatInfo.Mp4;
        }

        return MediaFormatInfo.Unknown;
    }

    private static bool ContainsHeicBrand(ReadOnlySpan<byte> payload)
    {
        for (int offset = 8; offset + 4 <= payload.Length; offset += 4)
        {
            if (IsHeicBrand(payload.Slice(offset, 4)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsMovieBrand(ReadOnlySpan<byte> payload)
    {
        for (int offset = 8; offset + 4 <= payload.Length; offset += 4)
        {
            if (IsMovieBrand(payload.Slice(offset, 4)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsBrand(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> brand)
    {
        for (int offset = 8; offset + 4 <= payload.Length; offset += 4)
        {
            if (payload.Slice(offset, 4).SequenceEqual(brand))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHeicBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("heic"u8) ||
        brand.SequenceEqual("heix"u8) ||
        brand.SequenceEqual("hevc"u8) ||
        brand.SequenceEqual("hevx"u8) ||
        brand.SequenceEqual("heim"u8) ||
        brand.SequenceEqual("heis"u8) ||
        brand.SequenceEqual("hevm"u8) ||
        brand.SequenceEqual("hevs"u8) ||
        brand.SequenceEqual("mif1"u8) ||
        brand.SequenceEqual("msf1"u8);

    private static bool IsAvifBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8);

    private static bool IsMovieBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("isom"u8) ||
        brand.SequenceEqual("iso2"u8) ||
        brand.SequenceEqual("iso3"u8) ||
        brand.SequenceEqual("iso4"u8) ||
        brand.SequenceEqual("iso5"u8) ||
        brand.SequenceEqual("iso6"u8) ||
        brand.SequenceEqual("mp41"u8) ||
        brand.SequenceEqual("mp42"u8) ||
        brand.SequenceEqual("avc1"u8) ||
        brand.SequenceEqual("dash"u8) ||
        brand.SequenceEqual("M4V "u8) ||
        brand.SequenceEqual("M4VP"u8) ||
        brand.SequenceEqual("MSNV"u8) ||
        brand.SequenceEqual("F4V "u8) ||
        brand.SequenceEqual("3gp4"u8) ||
        brand.SequenceEqual("3gp5"u8) ||
        brand.SequenceEqual("3g2a"u8) ||
        brand.SequenceEqual("mmp4"u8) ||
        brand.SequenceEqual("mjp2"u8);

    private static MediaDecodeException Missing(string path, Exception inner) =>
        new($"Le fichier « {path} » est introuvable.", "fichier-introuvable", path, inner);
}
