using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace Memo.App.Controls;

public sealed record MapPoint(double Latitude, double Longitude, long MemoryId, string Label);

public sealed class MemoMap : UserControl
{
    private static readonly Lazy<List<double[][]>> Land = new(LoadLand);
    private readonly Image _image = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill };
    private readonly List<(MapPoint Point, double X, double Y)> _projected = new();
    private IReadOnlyList<MapPoint> _points = Array.Empty<MapPoint>();
    private double _minLon = -180, _maxLon = 180, _minLat = -60, _maxLat = 84;
    private double _scale = 1;

    public event EventHandler<MapPoint>? PointSelected;

    public MemoMap()
    {
        Content = _image;
        IsTabStop = true;
        ActualThemeChanged += (_, _) => Render();
        SizeChanged += (_, _) => Render();
        PointerPressed += OnPointerPressed;
        Loaded += (_, _) => Render();
    }

    public void SetPoints(IReadOnlyList<MapPoint> points)
    {
        _points = points;
        Render();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(this).Position;
        var closest = _projected
            .Select(p => (p.Point, Distance: Math.Sqrt(Math.Pow(p.X - pos.X, 2) + Math.Pow(p.Y - pos.Y, 2))))
            .OrderBy(p => p.Distance)
            .FirstOrDefault();
        if (closest.Point is not null && closest.Distance <= 16)
        {
            PointSelected?.Invoke(this, closest.Point);
        }
    }

    private void Render()
    {
        if (ActualWidth < 8 || ActualHeight < 8)
        {
            return;
        }

        var scale = (float)(XamlRoot?.RasterizationScale ?? 1.0);
        _scale = scale;
        var pw = Math.Max(1, (int)(ActualWidth * scale));
        var ph = Math.Max(1, (int)(ActualHeight * scale));

        ComputeBounds();

        using var bitmap = new SKBitmap(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);

        var dark = ActualTheme == ElementTheme.Dark;
        var water = dark ? new SKColor(0x14, 0x12, 0x10) : new SKColor(0xF4, 0xF1, 0xEC);
        var land = dark ? new SKColor(0x2E, 0x2A, 0x24) : new SKColor(0xE6, 0xDF, 0xD2);
        var coast = dark ? new SKColor(0x3E, 0x38, 0x30) : new SKColor(0xCF, 0xC5, 0xB4);
        var accent = dark ? new SKColor(0xE3, 0x9A, 0x5C) : new SKColor(0xA6, 0x4B, 0x1E);

        canvas.Clear(water);

        using var landPaint = new SKPaint { Color = land, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var coastPaint = new SKPaint { Color = coast, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f * scale };

        var lonSpan = Math.Max(0.0001, _maxLon - _minLon);
        var latSpan = Math.Max(0.0001, _maxLat - _minLat);

        foreach (var ring in Land.Value)
        {
            using var path = new SKPath();
            var started = false;
            foreach (var pair in ring)
            {
                var x = (float)((pair[0] - _minLon) / lonSpan * pw);
                var y = (float)((_maxLat - pair[1]) / latSpan * ph);
                if (!started)
                {
                    path.MoveTo(x, y);
                    started = true;
                }
                else
                {
                    path.LineTo(x, y);
                }
            }
            if (started)
            {
                path.Close();
                canvas.DrawPath(path, landPaint);
                canvas.DrawPath(path, coastPaint);
            }
        }

        _projected.Clear();
        using var dot = new SKPaint { Color = accent, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var ringPaint = new SKPaint { Color = water, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f * scale };
        foreach (var point in _points)
        {
            var x = (float)((point.Longitude - _minLon) / lonSpan * pw);
            var y = (float)((_maxLat - point.Latitude) / latSpan * ph);
            canvas.DrawCircle(x, y, 7f * scale, dot);
            canvas.DrawCircle(x, y, 7f * scale, ringPaint);
            _projected.Add((point, x / scale, y / scale));
        }

        var writeable = new WriteableBitmap(pw, ph);
        using (var stream = writeable.PixelBuffer.AsStream())
        {
            var pixels = bitmap.Bytes;
            var rowBytes = pw * 4;
            for (var y = 0; y < ph; y++)
            {
                stream.Write(pixels, y * bitmap.RowBytes, rowBytes);
            }
        }
        writeable.Invalidate();
        _image.Source = writeable;
    }

    private void ComputeBounds()
    {
        if (_points.Count == 0)
        {
            _minLon = -180; _maxLon = 180; _minLat = -60; _maxLat = 84;
            return;
        }

        var minLon = _points.Min(p => p.Longitude);
        var maxLon = _points.Max(p => p.Longitude);
        var minLat = _points.Min(p => p.Latitude);
        var maxLat = _points.Max(p => p.Latitude);

        var lonSpan = Math.Max(6, maxLon - minLon);
        var latSpan = Math.Max(6, maxLat - minLat);
        var lonCenter = (minLon + maxLon) / 2;
        var latCenter = (minLat + maxLat) / 2;

        var aspect = ActualWidth / Math.Max(1, ActualHeight);
        if (lonSpan / latSpan < aspect)
        {
            lonSpan = latSpan * aspect;
        }
        else
        {
            latSpan = lonSpan / aspect;
        }

        _minLon = Math.Max(-180, lonCenter - lonSpan / 2);
        _maxLon = Math.Min(180, lonCenter + lonSpan / 2);
        _minLat = Math.Max(-85, latCenter - latSpan / 2);
        _maxLat = Math.Min(85, latCenter + latSpan / 2);
    }

    private static List<double[][]> LoadLand()
    {
        var result = new List<double[][]>();
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Map", "countries.geojson");
        if (!File.Exists(path))
        {
            return result;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            var geometry = feature.GetProperty("geometry");
            var type = geometry.GetProperty("type").GetString();
            if (type == "Polygon")
            {
                foreach (var ring in geometry.GetProperty("coordinates").EnumerateArray())
                {
                    result.Add(ReadRing(ring));
                }
            }
            else if (type == "MultiPolygon")
            {
                foreach (var polygon in geometry.GetProperty("coordinates").EnumerateArray())
                {
                    foreach (var ring in polygon.EnumerateArray())
                    {
                        result.Add(ReadRing(ring));
                    }
                }
            }
        }
        return result;
    }

    private static double[][] ReadRing(JsonElement ring)
    {
        var points = new List<double[]>();
        foreach (var coordinate in ring.EnumerateArray())
        {
            if (coordinate.GetArrayLength() >= 2)
            {
                points.Add(new[] { coordinate[0].GetDouble(), coordinate[1].GetDouble() });
            }
        }
        return points.ToArray();
    }
}

