using System.Buffers.Binary;
using SkiaSharp;

namespace Memo.Media;

/// <summary>
/// Application de l'orientation EXIF (tag 274) à une image déjà décodée dans son sens encodé.
/// L'orientation est appliquée exactement une fois, après le décodage brut des pixels.
/// </summary>
public static class ExifOrientation
{
    /// <summary>
    /// Angle approximatif dans le sens horaire. Les orientations 2, 4, 5 et 7 comportent un miroir :
    /// l'angle sert aux dimensions et à la géométrie, jamais à remplacer <see cref="Apply"/>.
    /// </summary>
    public static int ToRotationDegrees(SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.TopRight => 0,
        SKEncodedOrigin.BottomRight => 180,
        SKEncodedOrigin.BottomLeft => 180,
        SKEncodedOrigin.LeftTop => 90,
        SKEncodedOrigin.RightTop => 90,
        SKEncodedOrigin.RightBottom => 270,
        SKEncodedOrigin.LeftBottom => 270,
        _ => 0,
    };

    /// <summary>Indique si l'orientation échange largeur et hauteur.</summary>
    public static bool SwapsDimensions(SKEncodedOrigin origin) => origin is
        SKEncodedOrigin.LeftTop or
        SKEncodedOrigin.RightTop or
        SKEncodedOrigin.RightBottom or
        SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Retourne une image dans le sens d'affichage. Pour <see cref="SKEncodedOrigin.TopLeft"/> (et
    /// <see cref="SKEncodedOrigin.Default"/>), la source elle-même est retournée sans copie.
    /// Sinon une nouvelle image est créée et la source reste la propriété de l'appelant.
    /// </summary>
    public static SKBitmap Apply(SKBitmap source, SKEncodedOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0)
        {
            throw new ArgumentException("L'image source est vide.", nameof(source));
        }

        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
        {
            return source;
        }

        int width = source.Width;
        int height = source.Height;
        bool swap = SwapsDimensions(origin);
        int targetWidth = swap ? height : width;
        int targetHeight = swap ? width : height;

        var target = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, source.ColorType, source.AlphaType));
        try
        {
            using (var canvas = new SKCanvas(target))
            using (var paint = new SKPaint { IsAntialias = false })
            {
                canvas.Clear(SKColors.Transparent);
                canvas.SetMatrix(CreateMatrix(origin, width, height));
                canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
                canvas.Flush();
            }

            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static SKMatrix CreateMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        // Miroir horizontal.
        SKEncodedOrigin.TopRight => new SKMatrix
        {
            ScaleX = -1, SkewX = 0, TransX = width,
            SkewY = 0, ScaleY = 1, TransY = 0,
            Persp2 = 1,
        },

        // Rotation 180°.
        SKEncodedOrigin.BottomRight => new SKMatrix
        {
            ScaleX = -1, SkewX = 0, TransX = width,
            SkewY = 0, ScaleY = -1, TransY = height,
            Persp2 = 1,
        },

        // Miroir vertical.
        SKEncodedOrigin.BottomLeft => new SKMatrix
        {
            ScaleX = 1, SkewX = 0, TransX = 0,
            SkewY = 0, ScaleY = -1, TransY = height,
            Persp2 = 1,
        },

        // Transposition (miroir diagonal haut-gauche / bas-droite).
        SKEncodedOrigin.LeftTop => new SKMatrix
        {
            ScaleX = 0, SkewX = 1, TransX = 0,
            SkewY = 1, ScaleY = 0, TransY = 0,
            Persp2 = 1,
        },

        // Rotation 90° horaire.
        SKEncodedOrigin.RightTop => new SKMatrix
        {
            ScaleX = 0, SkewX = -1, TransX = height,
            SkewY = 1, ScaleY = 0, TransY = 0,
            Persp2 = 1,
        },

        // Transversale (miroir diagonal haut-droite / bas-gauche).
        SKEncodedOrigin.RightBottom => new SKMatrix
        {
            ScaleX = 0, SkewX = -1, TransX = height,
            SkewY = -1, ScaleY = 0, TransY = width,
            Persp2 = 1,
        },

        // Rotation 270° horaire (90° antihoraire).
        SKEncodedOrigin.LeftBottom => new SKMatrix
        {
            ScaleX = 0, SkewX = 1, TransX = 0,
            SkewY = -1, ScaleY = 0, TransY = width,
            Persp2 = 1,
        },

        _ => SKMatrix.CreateIdentity(),
    };
}

/// <summary>
/// Lecture minimale du tag EXIF 274 dans un JPEG, un PNG (chunk eXIf) ou un WebP (chunk EXIF).
/// Un bloc EXIF absent ou mal formé n'empêche jamais le décodage de l'image.
/// </summary>
internal static class ExifOrientationReader
{
    public static SKEncodedOrigin Read(ReadOnlySpan<byte> data, MediaFormatKind format)
    {
        try
        {
            return format switch
            {
                MediaFormatKind.Jpeg => ReadJpeg(data),
                MediaFormatKind.Png => ReadPng(data),
                MediaFormatKind.WebP => ReadWebP(data),
                _ => SKEncodedOrigin.Default,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return SKEncodedOrigin.Default;
        }
        catch (IndexOutOfRangeException)
        {
            return SKEncodedOrigin.Default;
        }
    }

    private static SKEncodedOrigin ReadJpeg(ReadOnlySpan<byte> data)
    {
        int position = 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            byte marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            position += 2;
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }

            if (marker is 0xDA or 0xD9)
            {
                break;
            }

            if (position + 2 > data.Length)
            {
                break;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
            if (segmentLength < 2 || position + segmentLength > data.Length)
            {
                break;
            }

            ReadOnlySpan<byte> payload = data.Slice(position + 2, segmentLength - 2);
            if (marker == 0xE1 && payload.StartsWith("Exif\0\0"u8))
            {
                return ReadTiff(payload[6..]);
            }

            position += segmentLength;
        }

        return SKEncodedOrigin.Default;
    }

    private static SKEncodedOrigin ReadPng(ReadOnlySpan<byte> data)
    {
        int position = 8;
        while (position + 12 <= data.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position, 4));
            long chunkEnd = (long)position + 12 + length;
            if (length > int.MaxValue || chunkEnd > data.Length)
            {
                break;
            }

            ReadOnlySpan<byte> chunkType = data.Slice(position + 4, 4);
            if (chunkType.SequenceEqual("eXIf"u8))
            {
                return ReadTiff(data.Slice(position + 8, (int)length));
            }

            position += 12 + (int)length;
        }

        return SKEncodedOrigin.Default;
    }

    private static SKEncodedOrigin ReadWebP(ReadOnlySpan<byte> data)
    {
        int position = 12;
        while (position + 8 <= data.Length)
        {
            ReadOnlySpan<byte> fourCc = data.Slice(position, 4);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 4, 4));
            long chunkEnd = (long)position + 8 + length;
            if (length > int.MaxValue || chunkEnd > data.Length)
            {
                break;
            }

            if (fourCc.SequenceEqual("EXIF"u8))
            {
                ReadOnlySpan<byte> payload = data.Slice(position + 8, (int)length);
                if (payload.StartsWith("Exif\0\0"u8))
                {
                    payload = payload[6..];
                }

                return ReadTiff(payload);
            }

            position += 8 + (int)length + (int)(length & 1);
        }

        return SKEncodedOrigin.Default;
    }

    private static SKEncodedOrigin ReadTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return SKEncodedOrigin.Default;
        }

        bool littleEndian;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I')
        {
            littleEndian = true;
        }
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M')
        {
            littleEndian = false;
        }
        else
        {
            return SKEncodedOrigin.Default;
        }

        ushort magic = ReadUInt16(tiff, 2, littleEndian);
        if (magic != 42)
        {
            return SKEncodedOrigin.Default;
        }

        uint ifdOffset = ReadUInt32(tiff, 4, littleEndian);
        if (ifdOffset < 8 || ifdOffset + 2 > tiff.Length)
        {
            return SKEncodedOrigin.Default;
        }

        int entryOffset = (int)ifdOffset;
        ushort entryCount = ReadUInt16(tiff, entryOffset, littleEndian);
        entryOffset += 2;

        for (int i = 0; i < entryCount; i++)
        {
            if (entryOffset + 12 > tiff.Length)
            {
                break;
            }

            ushort tag = ReadUInt16(tiff, entryOffset, littleEndian);
            if (tag == 0x0112)
            {
                ushort type = ReadUInt16(tiff, entryOffset + 2, littleEndian);
                uint count = ReadUInt32(tiff, entryOffset + 4, littleEndian);
                if (type != 3 || count < 1)
                {
                    return SKEncodedOrigin.Default;
                }

                ushort value = ReadUInt16(tiff, entryOffset + 8, littleEndian);
                return value is >= 1 and <= 8 ? (SKEncodedOrigin)value : SKEncodedOrigin.Default;
            }

            entryOffset += 12;
        }

        return SKEncodedOrigin.Default;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
}
