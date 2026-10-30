using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using SkiaSharp.Views.Windows;

namespace Memo.App.Controls;

/// <summary>
/// Scène de lecture unifiée : photo ou vidéo, calque optionnel, composition
/// dans le même repère. Les frames vidéo viennent de LibVLC en mémoire, ce qui
/// évite une fenêtre de sortie séparée et garantit l'alignement du calque.
/// </summary>
public sealed class MediaStage : UserControl
{
    private readonly SKXamlCanvas _canvas = new();
    private readonly object _frameLock = new();
    private readonly DispatcherQueue _dispatcher;

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private LibVLCSharp.Shared.Media? _media;
    private SKBitmap? _frame;
    private SKBitmap? _photo;
    private SKBitmap? _overlay;
    private IntPtr _buffer;
    private int _bufferSize;
    private int _width;
    private int _height;
    private bool _overlayVisible = true;
    private bool _invalidateQueued;
    private bool _disposed;

    public event EventHandler? Ended;

    public MediaStage()
    {
        Content = _canvas;
        _canvas.PaintSurface += OnPaintSurface;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

    }

    public bool IsVideo => _player is not null && _media is not null;
    public bool Mute
    {
        get => _player?.Mute ?? false;
        set { if (_player is not null) _player.Mute = value; }
    }
    public bool IsPlaying => _player?.IsPlaying == true;
    public long LengthMs => _player?.Length ?? 0;
    public double Position => _player is null || _player.Length <= 0 ? 0 : Math.Clamp((double)_player.Time / _player.Length, 0, 1);

    public void ShowPhoto(string path)
    {
        StopVideo();
        _photo?.Dispose();
        _photo = Decode(path);
        _frame = null;
        Invalidate();
    }

    public void PlayVideo(string path)
    {
        StopVideo();
        _photo?.Dispose();
        _photo = null;
        EnsurePlayer();
        if (_libVlc is null || _player is null)
        {
            return;
        }

        _media = new LibVLCSharp.Shared.Media(_libVlc, new Uri(path));
        _player.Play(_media);
    }

    public void Pause()
    {
        if (_player is not null && _player.IsPlaying)
        {
            _player.Pause();
        }
    }

    public void Resume()
    {
        if (_player is not null && !_player.IsPlaying)
        {
            _player.Play();
        }
    }

    public void StopVideo()
    {
        if (_player is not null)
        {
            try
            {
                if (_player.IsPlaying)
                {
                    _player.Stop();
                }
            }
            catch
            {
                // la sortie native peut déjà être libérée
            }
        }
        _media?.Dispose();
        _media = null;
        lock (_frameLock)
        {
            _frame = null;
        }
    }

    public void SetOverlay(string? path)
    {
        _overlay?.Dispose();
        _overlay = path is null ? null : Decode(path);
        Invalidate();
    }

    public void SetOverlayVisible(bool visible)
    {
        _overlayVisible = visible;
        Invalidate();
    }

    public void Clear()
    {
        StopVideo();
        _photo?.Dispose();
        _photo = null;
        SetOverlay(null);
        Invalidate();
    }

    private void EnsurePlayer()
    {
        if (_player is not null)
        {
            return;
        }

        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--quiet", "--avcodec-hw=none");
        _player = new MediaPlayer(_libVlc);
        _player.EndReached += OnEndReached;
        _player.SetVideoFormatCallbacks(
            new MediaPlayer.LibVLCVideoFormatCb(OnFormat),
            new MediaPlayer.LibVLCVideoCleanupCb(OnCleanup));
        _player.SetVideoCallbacks(
            new MediaPlayer.LibVLCVideoLockCb(OnLock),
            null,
            new MediaPlayer.LibVLCVideoDisplayCb(OnDisplay));
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // RV32 = BGRA sur little-endian, format natif des callbacks LibVLC.
        Marshal.Copy(new[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
        pitches = width * 4;
        lines = height;
        _width = (int)width;
        _height = (int)height;

        var size = _width * _height * 4;
        if (_bufferSize != size)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
            }
            _buffer = Marshal.AllocHGlobal(size);
            _bufferSize = size;
        }

        lock (_frameLock)
        {
            if (_frame is null || _frame.Width != _width || _frame.Height != _height)
            {
                // L'ancienne image n'est pas libérée immédiatement : un rendu peut
                // encore la référencer sur le thread UI.
                _frame = new SKBitmap(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            }
        }

        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        // Le tampon est conservé pour la session suivante ; libéré à la destruction.
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.WriteIntPtr(planes, 0, _buffer);
        }
        return _buffer;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        if (_buffer == IntPtr.Zero || _width == 0 || _height == 0)
        {
            return;
        }

        lock (_frameLock)
        {
            if (_frame is null)
            {
                return;
            }
            var destination = _frame.GetPixels();
            if (destination != IntPtr.Zero)
            {
                unsafe
                {
                    Buffer.MemoryCopy((void*)_buffer, (void*)destination, _bufferSize, _bufferSize);
                }
            }
        }

        QueueInvalidate();
    }

    private void QueueInvalidate()
    {
        if (_invalidateQueued)
        {
            return;
        }
        _invalidateQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _invalidateQueued = false;
            _canvas.Invalidate();
        });
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x0E, 0x0D, 0x0C));

        using var paint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        lock (_frameLock)
        {
            var source = _frame ?? _photo;
            if (source is null || source.Width == 0 || source.Height == 0)
            {
                return;
            }

            var bounds = new SKRect(0, 0, e.Info.Width, e.Info.Height);
            var scale = Math.Min(bounds.Width / source.Width, bounds.Height / source.Height);
            var width = source.Width * scale;
            var height = source.Height * scale;
            var dest = new SKRect(
                bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2,
                bounds.Left + (bounds.Width + width) / 2,
                bounds.Top + (bounds.Height + height) / 2);

            canvas.DrawBitmap(source, dest, sampling, paint);

            if (_overlayVisible && _overlay is not null)
            {
                canvas.DrawBitmap(_overlay, dest, sampling, paint);
            }
        }
    }

    private void Invalidate() => _canvas.Invalidate();

    private void OnEndReached(object? sender, EventArgs e)
    {
        _dispatcher.TryEnqueue(() => Ended?.Invoke(this, EventArgs.Empty));
    }

    private static SKBitmap? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return SKBitmap.Decode(stream);
        }
        catch
        {
            return null;
        }
    }

    private void DisposePlayer()
    {

        if (_player is not null)
        {
            _player.EndReached -= OnEndReached;
            try
            {
                _player.Stop();
            }
            catch
            {
            }
            _player.Dispose();
            _player = null;
        }
        _media?.Dispose();
        _media = null;
        _libVlc?.Dispose();
        _libVlc = null;

        _photo?.Dispose();
        _photo = null;
        _overlay?.Dispose();
        _overlay = null;
        _frame?.Dispose();
        _frame = null;

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }
}



