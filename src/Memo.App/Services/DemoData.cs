using Memo.Core;

namespace Memo.App.Services;

internal static class DemoData
{
    private const long Space = 1;

    public static IReadOnlyList<MemoryCard> CreateCards()
    {
        var list = new List<MemoryCard>();
        void Add(long id, DateOnly? date, MediaKind kind, MemoryStatus status, bool favorite = false,
                 bool excluded = false, long? duration = null, int? w = null, int? h = null,
                 string? format = null, long size = 0, int overlays = 0, double? lat = null,
                 double? lon = null, string? place = null, DateSource source = DateSource.Mp4CreationTime)
        {
            list.Add(new MemoryCard(
                id, Space, "Démo", $"DEMO-{id:0000}", kind, status, date,
                date is null ? DateSource.Unknown : source,
                date is null ? DatePrecision.Unknown : DatePrecision.Second,
                date is null ? DateMeaning.Unknown : DateMeaning.ContainerCreated,
                id, overlays > 0 ? 1000 + id : null, true, favorite, excluded,
                duration, w, h, format ?? (kind == MediaKind.Video ? "mp4" : "jpeg"),
                size, overlays, lat, lon, place));
        }

        Add(1, new DateOnly(2025, 10, 30), MediaKind.Photo, MemoryStatus.MediaOnly, favorite: true, w: 900, h: 1200, size: 248_000);
        Add(2, new DateOnly(2025, 10, 30), MediaKind.Video, MemoryStatus.MediaOnly, duration: 6200, w: 720, h: 1280, format: "mp4", size: 1_638_000);
        Add(3, new DateOnly(2024, 10, 30), MediaKind.Photo, MemoryStatus.Complete, w: 900, h: 1200, size: 310_000, overlays: 1, lat: 46.2044, lon: 6.1432, place: "Genève");
        Add(4, new DateOnly(2023, 10, 30), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 205_000, lat: 46.9480, lon: 7.4474, place: "Berne");
        Add(5, new DateOnly(2022, 10, 30), MediaKind.Video, MemoryStatus.MediaOnly, duration: 5100, w: 1280, h: 720, size: 1_214_000);
        Add(6, new DateOnly(2021, 10, 30), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 198_000);
        Add(7, new DateOnly(2021, 7, 14), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 264_000, lat: 46.5197, lon: 6.6323, place: "Lausanne");
        Add(8, new DateOnly(2020, 12, 31), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 221_000);
        Add(9, new DateOnly(2019, 6, 21), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 296_000, lat: 46.4312, lon: 6.9107, place: "Montreux");
        Add(10, new DateOnly(2018, 3, 16), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 187_000);
        Add(11, new DateOnly(2024, 2, 29), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 233_000);
        Add(12, new DateOnly(2020, 7, 14), MediaKind.Photo, MemoryStatus.Complete, w: 900, h: 1200, size: 242_000, overlays: 1);
        Add(13, new DateOnly(2019, 2, 14), MediaKind.Photo, MemoryStatus.Complete, w: 900, h: 1200, size: 252_000, overlays: 1);
        Add(14, null, MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 199_000);
        Add(15, null, MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 211_000);
        Add(16, null, MediaKind.Unknown, MemoryStatus.OverlayOnly, overlays: 1);
        Add(17, new DateOnly(2023, 12, 24), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 268_000, lat: 46.0207, lon: 7.7491, place: "Zermatt");
        Add(18, new DateOnly(2018, 6, 21), MediaKind.Video, MemoryStatus.MediaOnly, duration: 6000, w: 720, h: 1280, size: 1_638_000);
        Add(19, new DateOnly(2024, 7, 14), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 245_000, lat: 46.0037, lon: 8.9511, place: "Lugano");
        Add(20, new DateOnly(2022, 7, 14), MediaKind.Photo, MemoryStatus.MediaOnly, w: 900, h: 1200, size: 207_000, lat: 47.3769, lon: 8.5417, place: "Zurich");
        Add(21, new DateOnly(2021, 7, 14), MediaKind.Photo, MemoryStatus.MediaOnly, excluded: true, w: 900, h: 1200, size: 208_000);
        Add(22, new DateOnly(2019, 7, 14), MediaKind.Video, MemoryStatus.MediaOnly, duration: 6000, w: 720, h: 1280, size: 1_638_000);

        return list;
    }

    public static void FillAssets(Dictionary<long, string> thumbs, Dictionary<long, string> media, Dictionary<long, string> overlays)
    {
        for (long id = 1; id <= 22; id++)
        {
            var n = (int)((id - 1) % 10) + 1;
            thumbs[id] = $"ms-appx:///Assets/Demo/demo{n:00}.jpg";
        }
        media[2] = "ms-appx:///Assets/Demo/demo_video_portrait.mp4";
        media[5] = "ms-appx:///Assets/Demo/demo_video_landscape.mp4";
        media[18] = "ms-appx:///Assets/Demo/demo_video_portrait.mp4";
        media[22] = "ms-appx:///Assets/Demo/demo_video_portrait.mp4";
        overlays[3] = "ms-appx:///Assets/Demo/ovl02.png";
        overlays[12] = "ms-appx:///Assets/Demo/ovl01.png";
        overlays[13] = "ms-appx:///Assets/Demo/ovl02.png";
        overlays[16] = "ms-appx:///Assets/Demo/ovl01.png";
    }

    public static IReadOnlyList<long> AlbumMembers(long albumId) => albumId switch
    {
        1 => new long[] { 7, 12, 19, 20, 3, 4 },
        2 => new long[] { 9, 7, 19 },
        3 => new long[] { 1, 2 },
        _ => Array.Empty<long>()
    };

    public static IReadOnlyList<CollectionInfo> CreateAlbums() => new[]
    {
        new CollectionInfo(1, "Été 2021", CollectionKind.Album, DateTimeOffset.Now.AddMonths(-9), 6, "ms-appx:///Assets/Demo/demo05.jpg", new DateOnly(2021, 6, 1), new DateOnly(2021, 8, 31)),
        new CollectionInfo(2, "Montreux", CollectionKind.Album, DateTimeOffset.Now.AddMonths(-4), 3, "ms-appx:///Assets/Demo/demo07.jpg", new DateOnly(2019, 6, 21), new DateOnly(2024, 7, 14)),
        new CollectionInfo(3, "À garder", CollectionKind.Album, DateTimeOffset.Now.AddDays(-12), 2, "ms-appx:///Assets/Demo/demo03.jpg", null, null),
    };
}



