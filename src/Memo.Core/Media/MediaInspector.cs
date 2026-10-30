using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Memo.Core;

/// <summary>
/// Inspection binaire des médias importés. Aucun décodeur externe n'est requis : seules
/// la signature, les en-têtes de conteneur et les métadonnées EXIF utiles sont lues.
/// </summary>
public static class MediaInspector
{
    private const int MaxExifBytes = 16 * 1024 * 1024;
    private static readonly DateTimeOffset Epoch1904 =
        new(1904, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static MediaInfo InspectFile(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.SequentialScan);
        return Inspect(stream);
    }

    public static Task<MediaInfo> InspectFileAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.SequentialScan);
            return Inspect(stream);
        }, ct);
    }

    public static MediaInfo Inspect(Stream stream) => Inspect(stream, out _);

    internal static MediaInfo Inspect(Stream stream, out DateSource exifSource)
    {
        exifSource = DateSource.Unknown;
        if (!stream.CanSeek)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            return Inspect(copy, out exifSource);
        }

        var length = stream.Length;
        if (length < 12)
        {
            return Unknown();
        }

        var header = ReadAt(stream, 0, 32);
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return InspectJpeg(stream, length, out exifSource);
        }

        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return InspectPng(stream, length, out exifSource);
        }

        if (header.Length >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
        {
            return InspectWebp(stream, length, out exifSource);
        }

        if (LooksLikeIsoBmff(header))
        {
            return InspectIsoBmff(stream, length);
        }

        return Unknown();
    }

    private static MediaInfo Unknown() =>
        new("unknown", null, null, 0, null, false, null, null, null);

    private static bool LooksLikeIsoBmff(byte[] header)
    {
        if (header.Length < 8)
        {
            return false;
        }

        var type = Encoding.ASCII.GetString(header, 4, 4);
        return type is "ftyp" or "moov" or "mdat" or "free" or "skip" or "wide" or "meta";
    }

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || count <= 0 || offset >= stream.Length)
        {
            return Array.Empty<byte>();
        }

        var available = (int)Math.Min(count, stream.Length - offset);
        var buffer = new byte[available];
        stream.Position = offset;
        var read = 0;
        while (read < available)
        {
            var chunk = stream.Read(buffer, read, available - read);
            if (chunk <= 0)
            {
                break;
            }

            read += chunk;
        }

        return read == available ? buffer : buffer[..read];
    }

    private static ushort Be16(ReadOnlySpan<byte> data, int offset) =>
        offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..]) : (ushort)0;

    private static uint Be32(ReadOnlySpan<byte> data, int offset) =>
        offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..]) : 0;

    private static ulong Be64(ReadOnlySpan<byte> data, int offset) =>
        offset + 8 <= data.Length ? BinaryPrimitives.ReadUInt64BigEndian(data[offset..]) : 0;

    private static ushort Le16(ReadOnlySpan<byte> data, int offset) =>
        offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : (ushort)0;

    private static uint Le24(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 3 > data.Length)
        {
            return 0;
        }

        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16));
    }

    private static MediaInfo InspectJpeg(Stream stream, long length, out DateSource exifSource)
    {
        exifSource = DateSource.Unknown;
        int? width = null;
        int? height = null;
        var rotation = 0;
        var exif = default(ExifData?);
        var position = 2L;
        var guard = 0;

        while (position + 4 <= length && guard++ < 100_000)
        {
            var markerBytes = ReadAt(stream, position, 2);
            if (markerBytes.Length < 2 || markerBytes[0] != 0xFF)
            {
                position++;
                continue;
            }

            var marker = markerBytes[1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA)
            {
                break;
            }

            var lengthBytes = ReadAt(stream, position + 2, 2);
            if (lengthBytes.Length < 2)
            {
                break;
            }

            var segmentLength = Be16(lengthBytes, 0);
            if (segmentLength < 2)
            {
                break;
            }

            var payloadLength = segmentLength - 2;
            if (marker == 0xE1 && payloadLength > 6 && exif is null)
            {
                var payload = ReadAt(stream, position + 4, Math.Min(payloadLength, MaxExifBytes));
                if (payload.Length > 6 &&
                    payload[0] == (byte)'E' && payload[1] == (byte)'x' && payload[2] == (byte)'i' &&
                    payload[3] == (byte)'f' && payload[4] == 0 && payload[5] == 0)
                {
                    exif = ParseExifTiff(payload[6..]);
                }
            }

            if (IsSofMarker(marker) && payloadLength >= 5)
            {
                var sof = ReadAt(stream, position + 4, 5);
                if (sof.Length >= 5)
                {
                    height ??= Be16(sof, 1);
                    width ??= Be16(sof, 3);
                }
            }

            position += 2 + segmentLength;
        }

        ApplyExif(ref rotation, exif, out exifSource);
        return new MediaInfo(
            "jpeg",
            width,
            height,
            rotation,
            null,
            false,
            null,
            exif?.Date,
            exif?.Raw);
    }

    private static bool IsSofMarker(byte marker) =>
        marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static MediaInfo InspectPng(Stream stream, long length, out DateSource exifSource)
    {
        exifSource = DateSource.Unknown;
        int? width = null;
        int? height = null;
        var rotation = 0;
        var exif = default(ExifData?);

        if (length >= 33)
        {
            var ihdr = ReadAt(stream, 12, 13);
            if (ihdr.Length >= 13 && ihdr[0] == (byte)'I' && ihdr[1] == (byte)'H' &&
                ihdr[2] == (byte)'D' && ihdr[3] == (byte)'R')
            {
                width = (int)Math.Min(int.MaxValue, Be32(ihdr, 4));
                height = (int)Math.Min(int.MaxValue, Be32(ihdr, 8));
            }
        }

        var position = 8L;
        var guard = 0;
        while (position + 12 <= length && guard++ < 10_000)
        {
            var header = ReadAt(stream, position, 8);
            if (header.Length < 8)
            {
                break;
            }

            var chunkLength = Be32(header, 0);
            if (chunkLength > int.MaxValue)
            {
                break;
            }

            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (type == "eXIf" && chunkLength > 0)
            {
                var data = ReadAt(stream, position + 8, (int)Math.Min(chunkLength, MaxExifBytes));
                if (data.Length > 6 && data[0] == (byte)'E' && data[4] == 0)
                {
                    data = data[6..];
                }

                exif = ParseExifTiff(data);
            }

            if (type == "IEND")
            {
                break;
            }

            position += 12L + chunkLength;
        }

        ApplyExif(ref rotation, exif, out exifSource);
        return new MediaInfo(
            "png",
            width,
            height,
            rotation,
            null,
            false,
            null,
            exif?.Date,
            exif?.Raw);
    }

    private static MediaInfo InspectWebp(Stream stream, long length, out DateSource exifSource)
    {
        exifSource = DateSource.Unknown;
        int? width = null;
        int? height = null;
        var rotation = 0;
        var exif = default(ExifData?);

        var position = 12L;
        var guard = 0;
        while (position + 8 <= length && guard++ < 10_000)
        {
            var header = ReadAt(stream, position, 8);
            if (header.Length < 8)
            {
                break;
            }

            var type = Encoding.ASCII.GetString(header, 0, 4);
            var chunkLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (chunkLength < 0 || position + 8 + chunkLength > length)
            {
                break;
            }

            if (type == "VP8X" && chunkLength >= 10)
            {
                var data = ReadAt(stream, position + 8, 10);
                if (data.Length >= 10)
                {
                    width = (int)Le24(data, 4) + 1;
                    height = (int)Le24(data, 7) + 1;
                }
            }
            else if (type == "VP8 " && chunkLength >= 10)
            {
                var data = ReadAt(stream, position + 8, 10);
                if (data.Length >= 10 && data[3] == 0x9D && data[4] == 0x01 && data[5] == 0x2A)
                {
                    width = Le16(data, 6) & 0x3FFF;
                    height = Le16(data, 8) & 0x3FFF;
                }
            }
            else if (type == "VP8L" && chunkLength >= 5)
            {
                var data = ReadAt(stream, position + 8, 5);
                if (data.Length >= 5 && data[0] == 0x2F)
                {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(1));
                    width = (int)(bits & 0x3FFF) + 1;
                    height = (int)((bits >> 14) & 0x3FFF) + 1;
                }
            }
            else if (type == "EXIF" && chunkLength > 0)
            {
                var data = ReadAt(stream, position + 8, (int)Math.Min(chunkLength, MaxExifBytes));
                if (data.Length > 6 && data[0] == (byte)'E' && data[4] == 0)
                {
                    data = data[6..];
                }

                exif = ParseExifTiff(data);
            }

            position += 8 + chunkLength + (chunkLength & 1);
        }

        ApplyExif(ref rotation, exif, out exifSource);
        return new MediaInfo(
            "webp",
            width,
            height,
            rotation,
            null,
            false,
            null,
            exif?.Date,
            exif?.Raw);
    }

    private static void ApplyExif(ref int rotation, ExifData? exif, out DateSource source)
    {
        source = DateSource.Unknown;
        if (exif is null)
        {
            return;
        }

        rotation = exif.Orientation switch
        {
            3 or 4 => 180,
            5 or 6 => 90,
            7 or 8 => 270,
            _ => 0,
        };

        if (exif.Date is not null)
        {
            source = exif.Source;
        }
    }

    private sealed class IsoState
    {
        public string? MajorBrand { get; set; }
        public string Format { get; set; } = "mp4";
        public DateTimeOffset? CreationTime { get; set; }
        public long? DurationMs { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int Rotation { get; set; }
        public bool HasAudio { get; set; }
    }

    private static MediaInfo InspectIsoBmff(Stream stream, long length)
    {
        var state = new IsoState();
        var foundFtyp = false;
        var foundMoov = false;
        var position = 0L;
        var guard = 0;
        while (position + 8 <= length && guard++ < 100_000)
        {
            if (!TryReadBox(stream, position, length, out var type, out var contentStart, out var contentEnd))
            {
                break;
            }

            if (type == "ftyp")
            {
                ReadFtyp(stream, contentStart, contentEnd, state);
                foundFtyp = true;
            }
            else if (type == "moov")
            {
                ReadMoov(stream, contentStart, contentEnd, state);
                foundMoov = true;
            }
            else if (type == "meta")
            {
                ReadMeta(stream, contentStart, contentEnd, state);
            }

            if (contentEnd <= position)
            {
                break;
            }

            position = contentEnd;
        }

        if (!foundFtyp && !foundMoov)
        {
            return Unknown();
        }

        if (!foundFtyp)
        {
            state.Format = "mp4";
        }

        return new MediaInfo(
            state.Format,
            state.Width,
            state.Height,
            state.Rotation,
            state.DurationMs,
            state.HasAudio,
            state.CreationTime,
            null,
            null);
    }

    private static bool TryReadBox(
        Stream stream,
        long position,
        long end,
        out string type,
        out long contentStart,
        out long contentEnd)
    {
        type = string.Empty;
        contentStart = position;
        contentEnd = position;
        if (position + 8 > end)
        {
            return false;
        }

        var header = ReadAt(stream, position, 16);
        if (header.Length < 8)
        {
            return false;
        }

        var size = (long)Be32(header, 0);
        type = Encoding.ASCII.GetString(header, 4, 4);
        if (type.Any(ch => ch < 0x20 || ch > 0x7E))
        {
            return false;
        }

        var headerSize = 8L;
        if (size == 1)
        {
            if (header.Length < 16)
            {
                return false;
            }

            var large = Be64(header, 8);
            if (large > long.MaxValue)
            {
                return false;
            }

            size = (long)large;
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = end - position;
        }

        if (size < headerSize)
        {
            return false;
        }

        contentStart = position + headerSize;
        contentEnd = position + size;
        if (contentEnd > end || contentStart > contentEnd)
        {
            contentEnd = end;
        }

        return contentStart <= contentEnd;
    }

    private static void ReadFtyp(Stream stream, long start, long end, IsoState state)
    {
        var data = ReadAt(stream, start, (int)Math.Min(64, end - start));
        if (data.Length < 8)
        {
            return;
        }

        var major = Encoding.ASCII.GetString(data, 0, 4);
        state.MajorBrand = major;
        var brands = new List<string> { major };
        for (var offset = 8; offset + 4 <= data.Length; offset += 4)
        {
            brands.Add(Encoding.ASCII.GetString(data, offset, 4));
        }

        if (brands.Contains("qt  ", StringComparer.Ordinal))
        {
            state.Format = "mov";
        }
        else if (brands.Any(brand => brand is "avif" or "avis"))
        {
            state.Format = "avif";
        }
        else if (brands.Any(brand => brand is "heic" or "heix" or "hevc" or "hevx"
                     or "heim" or "heis" or "hevm" or "hevs" or "mif1" or "msf1"))
        {
            state.Format = "heic";
        }
        else
        {
            state.Format = "mp4";
        }
    }

    private static void ReadMoov(Stream stream, long start, long end, IsoState state)
    {
        var position = start;
        var guard = 0;
        while (position + 8 <= end && guard++ < 10_000)
        {
            if (!TryReadBox(stream, position, end, out var type, out var contentStart, out var contentEnd))
            {
                break;
            }

            if (type == "mvhd")
            {
                ReadMvhd(stream, contentStart, contentEnd, state);
            }
            else if (type == "trak")
            {
                ReadTrack(stream, contentStart, contentEnd, state);
            }

            if (contentEnd <= position)
            {
                break;
            }

            position = contentEnd;
        }
    }

    private static void ReadMvhd(Stream stream, long start, long end, IsoState state)
    {
        var data = ReadAt(stream, start, (int)Math.Min(40, end - start));
        if (data.Length < 20)
        {
            return;
        }

        var version = data[0];
        ulong creation;
        ulong timescale;
        ulong duration;
        if (version == 1)
        {
            if (data.Length < 32)
            {
                return;
            }

            creation = Be64(data, 4);
            timescale = Be32(data, 20);
            duration = Be64(data, 24);
        }
        else
        {
            creation = Be32(data, 4);
            timescale = Be32(data, 12);
            duration = Be32(data, 16);
        }

        if (creation > 0)
        {
            state.CreationTime = Epoch1904.AddSeconds(creation);
        }

        if (timescale > 0 && duration > 0 && duration != uint.MaxValue)
        {
            var milliseconds = (long)Math.Round(duration * 1000.0 / timescale);
            state.DurationMs = milliseconds;
        }
    }

    private sealed class TrackState
    {
        public string? Handler { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int Rotation { get; set; }
        public long? DurationMs { get; set; }
    }

    private static void ReadTrack(Stream stream, long start, long end, IsoState state)
    {
        var track = new TrackState();
        var position = start;
        var guard = 0;
        while (position + 8 <= end && guard++ < 10_000)
        {
            if (!TryReadBox(stream, position, end, out var type, out var contentStart, out var contentEnd))
            {
                break;
            }

            if (type == "tkhd")
            {
                ReadTkhd(stream, contentStart, contentEnd, track);
            }
            else if (type == "mdia")
            {
                ReadMdia(stream, contentStart, contentEnd, track);
            }

            if (contentEnd <= position)
            {
                break;
            }

            position = contentEnd;
        }

        if (track.Handler == "soun")
        {
            state.HasAudio = true;
        }

        if (track.Width is > 0 && track.Height is > 0)
        {
            var area = (long)track.Width.Value * track.Height.Value;
            var currentArea = state.Width is > 0 && state.Height is > 0
                ? (long)state.Width.Value * state.Height.Value
                : -1;
            if (area > currentArea)
            {
                state.Width = track.Width;
                state.Height = track.Height;
                state.Rotation = track.Rotation;
            }
            else if (state.Width is null)
            {
                state.Width = track.Width;
                state.Height = track.Height;
                state.Rotation = track.Rotation;
            }
        }

        if (state.DurationMs is null && track.DurationMs is not null)
        {
            state.DurationMs = track.DurationMs;
        }
    }

    private static void ReadTkhd(Stream stream, long start, long end, TrackState track)
    {
        var data = ReadAt(stream, start, (int)Math.Min(100, end - start));
        if (data.Length < 84)
        {
            return;
        }

        var version = data[0];
        var matrixOffset = version == 1 ? 52 : 40;
        var widthOffset = version == 1 ? 88 : 76;
        var heightOffset = version == 1 ? 92 : 80;
        if (data.Length < widthOffset + 8)
        {
            return;
        }

        var matrix = data.AsSpan(matrixOffset, 36);
        var a = BinaryPrimitives.ReadInt32BigEndian(matrix);
        var b = BinaryPrimitives.ReadInt32BigEndian(matrix[4..]);
        var angle = Math.Atan2(b, a) * 180.0 / Math.PI;
        var rounded = (int)Math.Round(angle / 90.0, MidpointRounding.AwayFromZero) * 90;
        track.Rotation = ((rounded % 360) + 360) % 360;

        var width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(widthOffset));
        var height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(heightOffset));
        track.Width = (int)(width >> 16);
        track.Height = (int)(height >> 16);
    }

    private static void ReadMdia(Stream stream, long start, long end, TrackState track)
    {
        var position = start;
        var guard = 0;
        while (position + 8 <= end && guard++ < 10_000)
        {
            if (!TryReadBox(stream, position, end, out var type, out var contentStart, out var contentEnd))
            {
                break;
            }

            if (type == "hdlr")
            {
                var data = ReadAt(stream, contentStart, (int)Math.Min(16, contentEnd - contentStart));
                if (data.Length >= 12)
                {
                    track.Handler = Encoding.ASCII.GetString(data, 8, 4);
                }
            }
            else if (type == "mdhd")
            {
                var data = ReadAt(stream, contentStart, (int)Math.Min(40, contentEnd - contentStart));
                if (data.Length >= 20)
                {
                    var version = data[0];
                    ulong timescale;
                    ulong duration;
                    if (version == 1 && data.Length >= 32)
                    {
                        timescale = Be32(data, 20);
                        duration = Be64(data, 24);
                    }
                    else
                    {
                        timescale = Be32(data, 12);
                        duration = Be32(data, 16);
                    }

                    if (timescale > 0 && duration > 0 && duration != uint.MaxValue)
                    {
                        track.DurationMs = (long)Math.Round(duration * 1000.0 / timescale);
                    }
                }
            }

            if (contentEnd <= position)
            {
                break;
            }

            position = contentEnd;
        }
    }

    private static void ReadMeta(Stream stream, long start, long end, IsoState state)
    {
        // meta est un FullBox dans HEIF : quatre octets de version/drapeaux précèdent ses enfants.
        var childStart = start + 4;
        if (!TryReadBox(stream, childStart, end, out _, out _, out _))
        {
            childStart = start;
        }

        var position = childStart;
        var guard = 0;
        while (position + 8 <= end && guard++ < 10_000)
        {
            if (!TryReadBox(stream, position, end, out var type, out var contentStart, out var contentEnd))
            {
                break;
            }

            if (type == "iprp")
            {
                ReadIprp(stream, contentStart, contentEnd, state);
            }

            if (contentEnd <= position)
            {
                break;
            }

            position = contentEnd;
        }
    }

    private static void ReadIprp(Stream stream, long start, long end, IsoState state)
    {
        var position = start;
        var guard = 0;
        while (position + 8 <= end && guard++ < 10_000)
        {
            if (!TryReadBox(stream, position, end, out var type, out var contentStart, out var contentEnd))
            {
                break;
            }

            if (type == "ipco")
            {
                ReadIpco(stream, contentStart, contentEnd, state);
            }

            if (contentEnd <= position)
            {
                break;
            }

            position = contentEnd;
        }
    }

    private static void ReadIpco(Stream stream, long start, long end, IsoState state)
    {
        var position = start;
        var guard = 0;
        while (position + 8 <= end && guard++ < 10_000)
        {
            if (!TryReadBox(stream, position, end, out var type, out var contentStart, out var contentEnd))
            {
                break;
            }

            if (type == "ispe")
            {
                var data = ReadAt(stream, contentStart, (int)Math.Min(12, contentEnd - contentStart));
                if (data.Length >= 12)
                {
                    var width = Be32(data, 4);
                    var height = Be32(data, 8);
                    if (width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue)
                    {
                        state.Width = (int)width;
                        state.Height = (int)height;
                    }
                }
            }
            else if (type == "irot")
            {
                var data = ReadAt(stream, contentStart, (int)Math.Min(5, contentEnd - contentStart));
                if (data.Length >= 5)
                {
                    state.Rotation = (data[4] & 0x03) * 90;
                }
            }

            if (contentEnd <= position)
            {
                break;
            }

            position = contentEnd;
        }
    }

    private sealed class ExifData
    {
        public int Orientation { get; init; }
        public DateOnly? Date { get; init; }
        public string? Raw { get; init; }
        public DateSource Source { get; init; }
    }

    private static ExifData? ParseExifTiff(byte[] tiff)
    {
        if (tiff.Length < 8)
        {
            return null;
        }

        var little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        var big = tiff[0] == (byte)'M' && tiff[1] == (byte)'M';
        if (!little && !big)
        {
            return null;
        }

        ushort U16(int offset)
        {
            if (offset < 0 || offset + 2 > tiff.Length)
            {
                return 0;
            }

            return little
                ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(offset))
                : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(offset));
        }

        uint U32(int offset)
        {
            if (offset < 0 || offset + 4 > tiff.Length)
            {
                return 0;
            }

            return little
                ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(offset))
                : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(offset));
        }

        string? Ascii(uint valueOffset, uint count)
        {
            if (count <= 4 || count > MaxExifBytes)
            {
                return null;
            }

            var offset = (int)valueOffset;
            if (offset < 0 || offset + count > tiff.Length)
            {
                return null;
            }

            var raw = Encoding.ASCII.GetString(tiff, offset, (int)count);
            var zero = raw.IndexOf('\0');
            if (zero >= 0)
            {
                raw = raw[..zero];
            }

            raw = raw.Trim();
            return raw.Length == 0 ? null : raw;
        }

        if (U16(2) != 42)
        {
            return null;
        }

        var ifd0 = (int)U32(4);
        if (ifd0 <= 0 || ifd0 >= tiff.Length)
        {
            return null;
        }

        var orientation = 0;
        string? dateTime = null;
        string? dateOriginal = null;
        uint exifIfd = 0;

        void ReadIfd(int offset, bool subIfd)
        {
            if (offset <= 0 || offset + 2 > tiff.Length)
            {
                return;
            }

            var count = U16(offset);
            if (count > 512)
            {
                return;
            }

            for (var index = 0; index < count; index++)
            {
                var entry = offset + 2 + index * 12;
                if (entry + 12 > tiff.Length)
                {
                    return;
                }

                var tag = U16(entry);
                var type = U16(entry + 2);
                var itemCount = U32(entry + 4);
                var valueOffset = U32(entry + 8);
                if (tag == 274 && type == 3)
                {
                    orientation = U16(entry + 8);
                }
                else if (tag == 306 && type == 2)
                {
                    dateTime ??= Ascii(valueOffset, itemCount);
                }
                else if (tag == 36867 && type == 2)
                {
                    dateOriginal ??= Ascii(valueOffset, itemCount);
                }
                else if (tag == 34665 && type == 4 && !subIfd)
                {
                    exifIfd = valueOffset;
                }
            }
        }

        ReadIfd(ifd0, false);
        if (exifIfd > 0)
        {
            ReadIfd((int)exifIfd, true);
        }

        var chosenRaw = dateOriginal ?? dateTime;
        var date = ParseExifDate(chosenRaw);
        var source = dateOriginal is not null && date is not null
            ? DateSource.ExifDateTimeOriginal
            : dateTime is not null && date is not null
                ? DateSource.ExifDateTime
                : DateSource.Unknown;

        return new ExifData
        {
            Orientation = orientation,
            Date = date,
            Raw = chosenRaw,
            Source = source,
        };
    }

    private static DateOnly? ParseExifDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var formats = new[]
        {
            "yyyy:MM:dd HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss",
            "yyyy:MM:dd HH:mm:ss.fff",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy:MM:dd",
            "yyyy-MM-dd",
        };
        return DateTime.TryParseExact(
            raw,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dateTime)
            ? DateOnly.FromDateTime(dateTime)
            : null;
    }
}
