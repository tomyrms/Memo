using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Memo.Core.Tests;

public abstract class TestBase
{
    protected string Root { get; private set; } = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "memo-core-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    protected string NewDirectory(string name = "library") =>
        Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);

    protected async Task<MemoLibrary> CreateLibraryAsync(
        string timeZone = "Europe/Paris",
        string name = "Bibliothèque de test")
    {
        return await MemoLibrary.CreateAsync(NewDirectory(), name, timeZone);
    }

    protected static async Task<SpaceInfo> NewSpaceAsync(MemoLibrary library, string name = "Compte principal")
    {
        return await library.Catalog.CreateSpaceAsync(name);
    }

    protected static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new AssertFailedException(
                $"Exception {exception.GetType().Name} au lieu de {typeof(TException).Name} : {exception.Message}");
        }

        throw new AssertFailedException($"Aucune exception {typeof(TException).Name} n'a été levée.");
    }
}

public static class MediaFactory
{
    public static byte[] Jpeg(
        int width = 320,
        int height = 240,
        DateOnly? date = null,
        string? dateTimeRaw = null,
        int? orientation = null)
    {
        var payload = new List<byte>();
        var hasExif = date is not null || dateTimeRaw is not null || orientation is not null;
        if (hasExif)
        {
            var tiff = BuildTiff(date, dateTimeRaw, orientation);
            payload.AddRange(Encoding.ASCII.GetBytes("Exif\0\0"));
            payload.AddRange(tiff);
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 0xFF, 0xD8 });
        if (hasExif)
        {
            WriteSegment(output, 0xE1, payload.ToArray());
        }

        var sof = new byte[9];
        sof[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(sof.AsSpan(3), (ushort)width);
        sof[5] = 1;
        sof[6] = 1;
        sof[7] = 0x11;
        sof[8] = 0;
        WriteSegment(output, 0xC0, sof);
        output.Write(new byte[] { 0xFF, 0xD9 });
        return output.ToArray();
    }

    public static byte[] Png(int width = 640, int height = 480)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WritePngChunk(output, "IHDR", ihdr);
        WritePngChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    public static byte[] Webp(int width = 1080, int height = 1920)
    {
        using var output = new MemoryStream();
        var vp8x = new byte[10];
        vp8x[0] = 0;
        WriteLe24(vp8x.AsSpan(4), width - 1);
        WriteLe24(vp8x.AsSpan(7), height - 1);

        var riffSize = 4 + 8 + vp8x.Length;
        output.Write(Encoding.ASCII.GetBytes("RIFF"));
        WriteUInt32Le(output, (uint)riffSize);
        output.Write(Encoding.ASCII.GetBytes("WEBP"));
        output.Write(Encoding.ASCII.GetBytes("VP8X"));
        WriteUInt32Le(output, (uint)vp8x.Length);
        output.Write(vp8x);
        return output.ToArray();
    }

    public static byte[] Mp4(
        DateTimeOffset? creationTimeUtc = null,
        int width = 1920,
        int height = 1080,
        int rotation = 0,
        int durationMs = 3000,
        bool hasAudio = true,
        string majorBrand = "isom",
        string compatibleBrands = "isomiso2mp41")
    {
        var ftyp = Box("ftyp", Concat(
            Encoding.ASCII.GetBytes(majorBrand),
            U32(512),
            Encoding.ASCII.GetBytes(compatibleBrands)));

        var mvhdContent = new MemoryStream();
        mvhdContent.WriteByte(0);
        mvhdContent.Write(new byte[3]);
        var creationSeconds = creationTimeUtc is null
            ? 0u
            : (uint)Math.Max(
                0,
                (creationTimeUtc.Value.UtcDateTime - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                .TotalSeconds);
        mvhdContent.Write(U32(creationSeconds));
        mvhdContent.Write(U32(creationSeconds));
        mvhdContent.Write(U32(1000));
        mvhdContent.Write(U32((uint)durationMs));
        mvhdContent.Write(U32(0x00010000));
        mvhdContent.Write(U16(0x0100));
        mvhdContent.Write(new byte[2]);
        mvhdContent.Write(new byte[8]);
        mvhdContent.Write(IdentityMatrix());
        mvhdContent.Write(new byte[24]);
        mvhdContent.Write(U32(3));
        var mvhd = Box("mvhd", mvhdContent.ToArray());

        var videoTrack = Track(
            trackId: 1,
            handler: "vide",
            width: width,
            height: height,
            rotation: rotation,
            durationMs: durationMs,
            creationSeconds: creationSeconds,
            volume: 0);
        var audioTrack = hasAudio
            ? Track(
                trackId: 2,
                handler: "soun",
                width: 0,
                height: 0,
                rotation: 0,
                durationMs: durationMs,
                creationSeconds: creationSeconds,
                volume: 0x0100)
            : Array.Empty<byte>();

        var moov = Box("moov", Concat(mvhd, videoTrack, audioTrack));
        return Concat(ftyp, moov);
    }

    public static byte[] Mov(
        DateTimeOffset? creationTimeUtc = null,
        int width = 1280,
        int height = 720,
        int rotation = 0,
        int durationMs = 2000,
        bool hasAudio = true) =>
        Mp4(creationTimeUtc, width, height, rotation, durationMs, hasAudio, "qt  ", "qt  ");

    public static byte[] Heic(int width = 4032, int height = 3024, int angle = 1)
    {
        var ispe = Box("ispe", Concat(new byte[4], U32((uint)width), U32((uint)height)));
        var irot = Box("irot", Concat(new byte[4], new[] { (byte)angle }));
        var ipco = Box("ipco", Concat(ispe, irot));
        var iprp = Box("iprp", ipco);
        var meta = Box("meta", Concat(new byte[4], iprp));
        var ftyp = Box("ftyp", Concat(
            Encoding.ASCII.GetBytes("heic"),
            U32(0),
            Encoding.ASCII.GetBytes("mif1heic")));
        return Concat(ftyp, meta);
    }

    private static byte[] Track(
        int trackId,
        string handler,
        int width,
        int height,
        int rotation,
        int durationMs,
        uint creationSeconds,
        int volume)
    {
        var tkhdContent = new MemoryStream();
        tkhdContent.WriteByte(0);
        tkhdContent.Write(new byte[3]);
        tkhdContent.Write(U32(creationSeconds));
        tkhdContent.Write(U32(creationSeconds));
        tkhdContent.Write(U32((uint)trackId));
        tkhdContent.Write(U32(0));
        tkhdContent.Write(U32((uint)durationMs));
        tkhdContent.Write(new byte[8]);
        tkhdContent.Write(U16(0));
        tkhdContent.Write(U16(0));
        tkhdContent.Write(U16((ushort)volume));
        tkhdContent.Write(U16(0));
        tkhdContent.Write(RotationMatrix(rotation));
        tkhdContent.Write(U32((uint)width << 16));
        tkhdContent.Write(U32((uint)height << 16));

        var hdlrContent = new MemoryStream();
        hdlrContent.Write(new byte[4]);
        hdlrContent.Write(U32(0));
        hdlrContent.Write(Encoding.ASCII.GetBytes(handler));
        hdlrContent.Write(new byte[12]);
        hdlrContent.Write(Encoding.ASCII.GetBytes("Handler\0"));

        var mdhdContent = new MemoryStream();
        mdhdContent.Write(new byte[4]);
        mdhdContent.Write(U32(creationSeconds));
        mdhdContent.Write(U32(creationSeconds));
        mdhdContent.Write(U32(1000));
        mdhdContent.Write(U32((uint)durationMs));
        mdhdContent.Write(U16(0x55C4));
        mdhdContent.Write(U16(0));

        var mdia = Box("mdia", Concat(
            Box("mdhd", mdhdContent.ToArray()),
            Box("hdlr", hdlrContent.ToArray())));
        return Box("trak", Concat(Box("tkhd", tkhdContent.ToArray()), mdia));
    }

    private static byte[] RotationMatrix(int rotation) => rotation switch
    {
        90 => Concat(I32(0), I32(65536), I32(0), I32(-65536), I32(0), I32(0), I32(0), I32(0), I32(0x40000000)),
        180 => Concat(I32(-65536), I32(0), I32(0), I32(0), I32(-65536), I32(0), I32(0), I32(0), I32(0x40000000)),
        270 => Concat(I32(0), I32(-65536), I32(0), I32(65536), I32(0), I32(0), I32(0), I32(0), I32(0x40000000)),
        _ => IdentityMatrix(),
    };

    private static byte[] IdentityMatrix() =>
        Concat(I32(65536), I32(0), I32(0), I32(0), I32(65536), I32(0), I32(0), I32(0), I32(0x40000000));

    private static byte[] BuildTiff(DateOnly? date, string? dateTimeRaw, int? orientation)
    {
        var dateText = dateTimeRaw ??
                       (date is null
                           ? null
                           : date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).Replace('-', ':') + " 12:34:56");
        var dateBytes = dateText is null
            ? Array.Empty<byte>()
            : Encoding.ASCII.GetBytes(dateText + "\0");
        var dateOriginal = date is not null;
        var hasDateTime = dateText is not null;
        var ifd0Count = (orientation is null ? 0 : 1) + (hasDateTime ? 1 : 0) + (dateOriginal ? 1 : 0);
        var ifd0Size = 2 + ifd0Count * 12 + 4;
        var subIfdOffset = 8 + ifd0Size;
        var subIfdSize = dateOriginal ? 2 + 12 + 4 : 0;
        var dataStart = 8 + ifd0Size + subIfdSize;
        var subIfdDateOffset = dataStart;
        var ifd0DateOffset = dateOriginal ? dataStart + dateBytes.Length : dataStart;

        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("II"));
        output.Write(LeU16(42));
        output.Write(LeU32(8));

        output.Write(LeU16((ushort)ifd0Count));
        if (orientation is not null)
        {
            output.Write(LeU16(0x0112));
            output.Write(LeU16(3));
            output.Write(LeU32(1));
            output.Write(LeU16((ushort)orientation.Value));
            output.Write(LeU16(0));
        }

        if (dateOriginal)
        {
            output.Write(LeU16(0x8769));
            output.Write(LeU16(4));
            output.Write(LeU32(1));
            output.Write(LeU32((uint)subIfdOffset));
        }

        if (hasDateTime)
        {
            output.Write(LeU16(0x0132));
            output.Write(LeU16(2));
            output.Write(LeU32((uint)dateBytes.Length));
            output.Write(LeU32((uint)ifd0DateOffset));
        }

        output.Write(LeU32(0));

        if (dateOriginal)
        {
            output.Write(LeU16(1));
            output.Write(LeU16(0x9003));
            output.Write(LeU16(2));
            output.Write(LeU32((uint)dateBytes.Length));
            output.Write(LeU32((uint)subIfdDateOffset));
            output.Write(LeU32(0));
        }

        if (dateText is not null)
        {
            output.Write(dateBytes);
            if (dateOriginal)
            {
                output.Write(dateBytes);
            }
        }

        return output.ToArray();
    }

    private static void WriteSegment(Stream output, byte marker, byte[] payload)
    {
        output.WriteByte(0xFF);
        output.WriteByte(marker);
        var length = payload.Length + 2;
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)length);
        output.Write(payload);
    }

    private static void WritePngChunk(Stream output, string type, byte[] data)
    {
        output.Write(U32((uint)data.Length));
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(Concat(typeBytes, data));
        output.Write(U32(crc));
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var content = Concat(parts);
        return Concat(U32((uint)(content.Length + 8)), Encoding.ASCII.GetBytes(type), content);
    }

    private static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] I32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] LeU16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] LeU32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static void WriteUInt32Le(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteLe24(Span<byte> destination, int value)
    {
        destination[0] = (byte)(value & 0xFF);
        destination[1] = (byte)((value >> 8) & 0xFF);
        destination[2] = (byte)((value >> 16) & 0xFF);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(part => part.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }
}

public static class ZipFactory
{
    public static async Task<string> CreateAsync(
        string path,
        DateTimeOffset? lastWriteTime = null,
        params (string Name, byte[] Data)[] entries)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            if (lastWriteTime is not null)
            {
                entry.LastWriteTime = lastWriteTime.Value;
            }

            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(data);
        }

        return path;
    }
}

public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public InlineProgress(Action<T> report)
    {
        _report = report;
    }

    public void Report(T value) => _report(value);
}

public static class PathFactory
{
    public static string WriteFile(string root, string relativePath, byte[] data)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, data);
        return path;
    }
}
