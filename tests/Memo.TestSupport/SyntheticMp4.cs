using System.Buffers.Binary;
using System.Text;

namespace Memo.TestSupport;

/// <summary>Vue minimaliste d'un MP4 synthétique, lue sans décodeur.</summary>
public sealed record SyntheticMp4Info(
    string MajorBrand,
    IReadOnlyList<string> TopLevelBoxes,
    DateTimeOffset? CreationTimeUtc,
    DateTimeOffset? ModificationTimeUtc,
    int Rotation,
    int EncodedWidth,
    int EncodedHeight,
    int DisplayWidth,
    int DisplayHeight,
    int DurationMs);

/// <summary>
/// Écrit un MP4 ISO-BMFF minimal et structurellement valide : ftyp, moov
/// (mvhd, tkhd, mdhd, hdlr, minf, stbl) et mdat. Les échantillons ne sont pas
/// décodables ; le fichier sert à tester la lecture du conteneur, des dates et
/// de la rotation, pas la lecture vidéo réelle.
/// </summary>
public static class SyntheticMp4
{
    private const int Timescale = 1000;
    private const int SamplePayloadSize = 1024;
    private static readonly DateTimeOffset MacEpoch = new(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Crée un MP4 synthétique en mémoire.</summary>
    /// <param name="creationTimeUtc">Date UTC écrite dans mvhd, tkhd et mdhd.</param>
    /// <param name="rotation">0 ou 90 degrés. La rotation 90 décrit un encodage paysage affiché en portrait.</param>
    /// <param name="encodedWidth">Largeur réellement encodée (paysage).</param>
    /// <param name="encodedHeight">Hauteur réellement encodée (paysage).</param>
    /// <param name="durationMs">Durée annoncée dans le conteneur.</param>
    public static byte[] Create(
        DateTimeOffset creationTimeUtc,
        int rotation = 0,
        int encodedWidth = 1920,
        int encodedHeight = 1080,
        int durationMs = 3000)
    {
        if (rotation is not (0 or 90))
        {
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Seules les rotations 0 et 90 sont générées.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encodedWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(encodedHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMs);

        var creation = ToMacTime(creationTimeUtc);
        var (displayWidth, displayHeight) = rotation == 90
            ? (encodedHeight, encodedWidth)
            : (encodedWidth, encodedHeight);

        using var ftyp = new MemoryStream();
        WriteBox(ftyp, "ftyp", stream =>
        {
            WriteFourCc(stream, "isom");
            WriteUInt32(stream, 0x0000_0200);
            WriteFourCc(stream, "isom");
            WriteFourCc(stream, "iso2");
            WriteFourCc(stream, "avc1");
            WriteFourCc(stream, "mp41");
        });

        long chunkOffsetField = -1;

        using var moov = new MemoryStream();
        WriteBox(moov, "moov", stream =>
        {
            WriteMovieHeader(stream, creation, durationMs);

            WriteBox(stream, "trak", trak =>
            {
                WriteTrackHeader(trak, creation, durationMs, displayWidth, displayHeight, rotation);

                WriteBox(trak, "mdia", mdia =>
                {
                    WriteMediaHeader(mdia, creation, durationMs);
                    WriteHandler(mdia);

                    WriteBox(mdia, "minf", minf =>
                    {
                        WriteVideoMediaHeader(minf);
                        WriteDataInformation(minf);

                        WriteBox(minf, "stbl", stbl =>
                        {
                            WriteSampleDescription(stbl, encodedWidth, encodedHeight);
                            WriteTimeToSample(stbl, durationMs);
                            WriteSampleToChunk(stbl);
                            WriteSampleSizes(stbl);
                            WriteChunkOffsets(stbl, position => chunkOffsetField = position);
                        });
                    });
                });
            });
        });

        if (chunkOffsetField < 0)
        {
            throw new InvalidOperationException("La table stco n'a pas été écrite.");
        }

        using var output = new MemoryStream();
        ftyp.Position = 0;
        ftyp.CopyTo(output);

        var moovStart = output.Position;
        moov.Position = 0;
        moov.CopyTo(output);

        var mdatHeaderStart = output.Position;
        var mdatDataStart = mdatHeaderStart + 8;

        // Le décalage de chunk pointe sur le premier octet de données, après l'en-tête mdat.
        output.Position = moovStart + chunkOffsetField;
        WriteUInt32(output, checked((uint)mdatDataStart));

        output.Position = mdatHeaderStart;
        WriteBox(output, "mdat", stream =>
        {
            var payload = BuildFakeSample();
            stream.Write(payload);
        });

        return output.ToArray();
    }

    /// <summary>Relit les propriétés utiles du MP4 synthétique sans décodeur vidéo.</summary>
    public static SyntheticMp4Info Inspect(ReadOnlySpan<byte> content)
    {
        if (content.Length < 12)
        {
            throw new InvalidDataException("Fichier trop court pour un conteneur ISO-BMFF.");
        }

        var data = content.ToArray();
        string? majorBrand = null;
        var topLevel = new List<string>();
        DateTimeOffset? creation = null;
        DateTimeOffset? modification = null;
        var rotation = 0;
        var encodedWidth = 0;
        var encodedHeight = 0;
        var displayWidth = 0;
        var displayHeight = 0;
        var durationMs = 0;

        foreach (var box in EnumerateBoxes(data, 0, data.Length))
        {
            topLevel.Add(box.Type);
            if (box.Type == "ftyp" && box.ContentLength >= 8)
            {
                majorBrand = ReadFourCc(data.AsSpan(box.ContentStart, 4));
            }
            else if (box.Type == "moov")
            {
                ReadMovieBox(data, box, ref creation, ref modification, ref rotation, ref encodedWidth, ref encodedHeight, ref displayWidth, ref displayHeight, ref durationMs);
            }
        }

        if (majorBrand is null)
        {
            throw new InvalidDataException("Boîte ftyp absente.");
        }

        return new SyntheticMp4Info(
            majorBrand,
            topLevel,
            creation,
            modification,
            rotation,
            encodedWidth,
            encodedHeight,
            displayWidth,
            displayHeight,
            durationMs);
    }

    /// <summary>Convertit un instant UTC en secondes depuis 1904, format des en-têtes MP4.</summary>
    public static uint ToMacTime(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var seconds = (utc - MacEpoch).TotalSeconds;
        if (seconds < 0 || seconds > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Date hors de la plage MP4 version 0 (1904–2040).");
        }

        return (uint)seconds;
    }

    /// <summary>Convertit un temps MP4 en instant UTC, pour les tests de lecture.</summary>
    public static DateTimeOffset FromMacTime(uint value) => MacEpoch.AddSeconds(value);

    private static DateTimeOffset FromMacTime64(ulong value) => MacEpoch.AddSeconds(value);

    private static void ReadMovieBox(
        byte[] content,
        Box box,
        ref DateTimeOffset? creation,
        ref DateTimeOffset? modification,
        ref int rotation,
        ref int encodedWidth,
        ref int encodedHeight,
        ref int displayWidth,
        ref int displayHeight,
        ref int durationMs)
    {
        foreach (var child in EnumerateBoxes(content, box.ContentStart, box.ContentStart + box.ContentLength))
        {
            switch (child.Type)
            {
                case "mvhd" when child.ContentLength >= 20:
                {
                    var payload = child.ContentStart;
                    var version = content[payload];
                    if (version == 1 && child.ContentLength >= 32)
                    {
                        creation = FromMacTime64(ReadUInt64(content.AsSpan(payload + 4, 8)));
                        modification = FromMacTime64(ReadUInt64(content.AsSpan(payload + 12, 8)));
                        var timescale = ReadUInt32(content.AsSpan(payload + 20, 4));
                        var duration = ReadUInt64(content.AsSpan(payload + 24, 8));
                        durationMs = timescale == 0 ? 0 : (int)Math.Min(int.MaxValue, duration * 1000 / timescale);
                    }
                    else
                    {
                        creation = FromMacTime(ReadUInt32(content.AsSpan(payload + 4, 4)));
                        modification = FromMacTime(ReadUInt32(content.AsSpan(payload + 8, 4)));
                        var timescale = ReadUInt32(content.AsSpan(payload + 12, 4));
                        var duration = ReadUInt32(content.AsSpan(payload + 16, 4));
                        durationMs = timescale == 0 ? 0 : (int)Math.Min(int.MaxValue, (ulong)duration * 1000 / timescale);
                    }

                    break;
                }

                case "trak":
                {
                    ReadTrackBox(content, child, ref rotation, ref encodedWidth, ref encodedHeight, ref displayWidth, ref displayHeight);
                    break;
                }
            }
        }
    }

    private static void ReadTrackBox(
        byte[] content,
        Box trak,
        ref int rotation,
        ref int encodedWidth,
        ref int encodedHeight,
        ref int displayWidth,
        ref int displayHeight)
    {
        foreach (var child in EnumerateBoxes(content, trak.ContentStart, trak.ContentStart + trak.ContentLength))
        {
            if (child.Type == "tkhd" && child.ContentLength >= 80)
            {
                var payload = child.ContentStart;
                var version = content[payload];
                var matrixOffset = version == 1 ? 52 : 40;
                var widthOffset = version == 1 ? 88 : 76;
                if (child.ContentLength >= widthOffset + 8)
                {
                    var a = ReadInt32(content.AsSpan(payload + matrixOffset, 4));
                    var b = ReadInt32(content.AsSpan(payload + matrixOffset + 4, 4));
                    var c = ReadInt32(content.AsSpan(payload + matrixOffset + 12, 4));
                    var d = ReadInt32(content.AsSpan(payload + matrixOffset + 16, 4));

                    rotation = a == 0 && b == 0x0001_0000 && c == -0x0001_0000 && d == 0 ? 90
                        : a == 0 && b == -0x0001_0000 && c == 0x0001_0000 && d == 0 ? 270
                        : a == -0x0001_0000 && d == -0x0001_0000 ? 180
                        : 0;

                    displayWidth = (int)(ReadUInt32(content.AsSpan(payload + widthOffset, 4)) >> 16);
                    displayHeight = (int)(ReadUInt32(content.AsSpan(payload + widthOffset + 4, 4)) >> 16);
                }
            }
            else if (child.Type == "mdia")
            {
                foreach (var mediaChild in EnumerateBoxes(content, child.ContentStart, child.ContentStart + child.ContentLength))
                {
                    if (mediaChild.Type != "minf")
                    {
                        continue;
                    }

                    foreach (var minfChild in EnumerateBoxes(content, mediaChild.ContentStart, mediaChild.ContentStart + mediaChild.ContentLength))
                    {
                        if (minfChild.Type != "stbl")
                        {
                            continue;
                        }

                        foreach (var stblChild in EnumerateBoxes(content, minfChild.ContentStart, minfChild.ContentStart + minfChild.ContentLength))
                        {
                            if (stblChild.Type != "stsd" || stblChild.ContentLength < 16)
                            {
                                continue;
                            }

                            foreach (var entry in EnumerateBoxes(content, stblChild.ContentStart + 8, stblChild.ContentStart + stblChild.ContentLength))
                            {
                                if (entry.Type is "avc1" or "hvc1" or "hev1" && entry.ContentLength >= 28)
                                {
                                    encodedWidth = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(entry.ContentStart + 24, 2));
                                    encodedHeight = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(entry.ContentStart + 26, 2));
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private static void WriteMovieHeader(Stream stream, uint creation, int durationMs)
    {
        WriteBox(stream, "mvhd", body =>
        {
            WriteUInt32(body, 0); // version 0, flags 0
            WriteUInt32(body, creation);
            WriteUInt32(body, creation);
            WriteUInt32(body, Timescale);
            WriteUInt32(body, checked((uint)durationMs));
            WriteUInt32(body, 0x0001_0000); // rate 1.0
            WriteUInt16(body, 0x0100); // volume 1.0
            WriteUInt16(body, 0);
            WriteUInt64(body, 0);
            WriteMatrix(body, rotation: 0);
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            WriteUInt32(body, 2); // prochain track ID
        });
    }

    private static void WriteTrackHeader(Stream stream, uint creation, int durationMs, int displayWidth, int displayHeight, int rotation)
    {
        WriteBox(stream, "tkhd", body =>
        {
            WriteUInt32(body, 0x0000_0007); // version 0, enabled | in movie | in preview
            WriteUInt32(body, creation);
            WriteUInt32(body, creation);
            WriteUInt32(body, 1); // track ID
            WriteUInt32(body, 0);
            WriteUInt32(body, checked((uint)durationMs));
            WriteUInt64(body, 0);
            WriteUInt16(body, 0); // layer
            WriteUInt16(body, 0); // alternate group
            WriteUInt16(body, 0); // volume vidéo
            WriteUInt16(body, 0);
            WriteMatrix(body, rotation);
            WriteUInt32(body, checked((uint)displayWidth << 16));
            WriteUInt32(body, checked((uint)displayHeight << 16));
        });
    }

    private static void WriteMediaHeader(Stream stream, uint creation, int durationMs)
    {
        WriteBox(stream, "mdhd", body =>
        {
            WriteUInt32(body, 0);
            WriteUInt32(body, creation);
            WriteUInt32(body, creation);
            WriteUInt32(body, Timescale);
            WriteUInt32(body, checked((uint)durationMs));
            WriteUInt16(body, 0x55C4); // "und"
            WriteUInt16(body, 0);
        });
    }

    private static void WriteHandler(Stream stream)
    {
        WriteBox(stream, "hdlr", body =>
        {
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            WriteFourCc(body, "vide");
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            WriteNullTerminated(body, "VideoHandler");
        });
    }

    private static void WriteVideoMediaHeader(Stream stream)
    {
        WriteBox(stream, "vmhd", body =>
        {
            WriteUInt32(body, 0x0000_0001);
            WriteUInt16(body, 0);
            WriteUInt16(body, 0);
            WriteUInt16(body, 0);
            WriteUInt16(body, 0);
        });
    }

    private static void WriteDataInformation(Stream stream)
    {
        WriteBox(stream, "dinf", dinf =>
        {
            WriteBox(dinf, "dref", dref =>
            {
                WriteUInt32(dref, 0);
                WriteUInt32(dref, 1);
                WriteBox(dref, "url ", url => WriteUInt32(url, 0x0000_0001));
            });
        });
    }

    private static void WriteSampleDescription(Stream stream, int encodedWidth, int encodedHeight)
    {
        WriteBox(stream, "stsd", stsd =>
        {
            WriteUInt32(stsd, 0);
            WriteUInt32(stsd, 1);
            WriteBox(stsd, "avc1", entry =>
            {
                WriteUInt16(entry, 0);
                WriteUInt16(entry, 0);
                WriteUInt16(entry, 0);
                WriteUInt16(entry, 1); // data reference index
                WriteUInt16(entry, 0);
                WriteUInt16(entry, 0);
                WriteUInt32(entry, 0);
                WriteUInt32(entry, 0);
                WriteUInt32(entry, 0);
                WriteUInt16(entry, checked((ushort)encodedWidth));
                WriteUInt16(entry, checked((ushort)encodedHeight));
                WriteUInt32(entry, 0x0048_0000); // 72 dpi
                WriteUInt32(entry, 0x0048_0000);
                WriteUInt32(entry, 0);
                WriteUInt16(entry, 1); // frame count
                WriteCompressorName(entry, "Synthetic");
                WriteUInt16(entry, 0x0018); // depth
                WriteUInt16(entry, 0xFFFF);
                WriteAvcConfiguration(entry);
            });
        });
    }

    private static void WriteAvcConfiguration(Stream stream)
    {
        WriteBox(stream, "avcC", body =>
        {
            body.WriteByte(1); // configurationVersion
            body.WriteByte(66); // Baseline profile
            body.WriteByte(0);
            body.WriteByte(30); // level 3.0
            body.WriteByte(0xFF); // 4 octets de longueur NAL
            body.WriteByte(0xE1); // un SPS
            WriteUInt16(body, 4);
            body.Write(new byte[] { 0x67, 0x42, 0x00, 0x1E });
            body.WriteByte(1); // un PPS
            WriteUInt16(body, 2);
            body.Write(new byte[] { 0x68, 0xCE });
        });
    }

    private static void WriteTimeToSample(Stream stream, int durationMs)
    {
        WriteBox(stream, "stts", body =>
        {
            WriteUInt32(body, 0);
            WriteUInt32(body, 1);
            WriteUInt32(body, 1);
            WriteUInt32(body, checked((uint)durationMs));
        });
    }

    private static void WriteSampleToChunk(Stream stream)
    {
        WriteBox(stream, "stsc", body =>
        {
            WriteUInt32(body, 0);
            WriteUInt32(body, 1);
            WriteUInt32(body, 1);
            WriteUInt32(body, 1);
            WriteUInt32(body, 1);
        });
    }

    private static void WriteSampleSizes(Stream stream)
    {
        WriteBox(stream, "stsz", body =>
        {
            WriteUInt32(body, 0);
            WriteUInt32(body, 0);
            WriteUInt32(body, 1);
            WriteUInt32(body, SamplePayloadSize);
        });
    }

    private static void WriteChunkOffsets(Stream stream, Action<long> recordFieldPosition)
    {
        WriteBox(stream, "stco", body =>
        {
            WriteUInt32(body, 0);
            WriteUInt32(body, 1);
            recordFieldPosition(body.Position);
            WriteUInt32(body, 0); // remplacé par le décalage final de mdat
        });
    }

    private static void WriteMatrix(Stream stream, int rotation)
    {
        // Matrice 3x3 en 16.16, sauf w en 2.30.
        switch (rotation)
        {
            case 0:
                WriteUInt32(stream, 0x0001_0000);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0x0001_0000);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0x4000_0000);
                break;
            case 90:
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0x0001_0000);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0xFFFF_0000); // -1 en 16.16
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0);
                WriteUInt32(stream, 0x4000_0000);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Rotation non générée.");
        }
    }

    private static byte[] BuildFakeSample()
    {
        var payload = new byte[SamplePayloadSize];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        // En-tête NAL factice : le conteneur reste lisible, l'échantillon n'est pas décodable.
        payload[0] = 0;
        payload[1] = 0;
        payload[2] = 0;
        payload[3] = 1;
        return payload;
    }

    private static void WriteCompressorName(Stream stream, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        var length = Math.Min(bytes.Length, 31);
        stream.WriteByte((byte)length);
        stream.Write(bytes, 0, length);
        for (var i = length; i < 31; i++)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteNullTerminated(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
    }

    private static void WriteBox(Stream stream, string type, Action<Stream> writeBody)
    {
        var start = stream.Position;
        WriteUInt32(stream, 0);
        WriteFourCc(stream, type);
        writeBody(stream);
        var end = stream.Position;
        var size = end - start;
        if (size > uint.MaxValue)
        {
            throw new InvalidOperationException("Boîte ISO-BMFF trop grande.");
        }

        stream.Position = start;
        WriteUInt32(stream, (uint)size);
        stream.Position = end;
    }

    private static IEnumerable<Box> EnumerateBoxes(byte[] content, int start, int end)
    {
        var offset = start;
        while (offset + 8 <= end)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(offset, 4));
            var type = ReadFourCc(content.AsSpan(offset + 4, 4));
            var headerSize = 8;
            ulong totalSize = size;

            if (size == 1)
            {
                if (offset + 16 > end)
                {
                    yield break;
                }

                totalSize = BinaryPrimitives.ReadUInt64BigEndian(content.AsSpan(offset + 8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                totalSize = (ulong)(end - offset);
            }

            if (totalSize < (ulong)headerSize || totalSize > (ulong)(end - offset))
            {
                yield break;
            }

            yield return new Box(type, offset + headerSize, checked((int)(totalSize - (ulong)headerSize)));
            offset += (int)totalSize;
        }
    }

    private static void WriteFourCc(Stream stream, string value)
    {
        if (value.Length != 4)
        {
            throw new ArgumentException("Un FourCC doit contenir exactement quatre caractères.", nameof(value));
        }

        stream.Write(Encoding.ASCII.GetBytes(value));
    }

    private static string ReadFourCc(ReadOnlySpan<byte> content) => Encoding.ASCII.GetString(content);

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> content) => BinaryPrimitives.ReadUInt32BigEndian(content);

    private static ulong ReadUInt64(ReadOnlySpan<byte> content) => BinaryPrimitives.ReadUInt64BigEndian(content);

    private static int ReadInt32(ReadOnlySpan<byte> content) => BinaryPrimitives.ReadInt32BigEndian(content);

    private readonly record struct Box(string Type, int ContentStart, int ContentLength);
}
