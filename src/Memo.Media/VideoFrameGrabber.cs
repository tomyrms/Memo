using System.Globalization;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using SkiaSharp;
using VlcCore = LibVLCSharp.Shared.Core;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Memo.Media;

/// <summary>Initialisation unique et thread-safe du moteur natif libvlc.</summary>
internal static class VlcRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;
    private static Exception? _failure;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            if (_failure is not null)
            {
                throw Failure(_failure);
            }

            try
            {
                // LibVLCSharp découvre libvlc/win-x64 copié par VideoLAN.LibVLC.Windows.
                VlcCore.Initialize();
                _initialized = true;
            }
            catch (Exception ex)
            {
                _failure = ex;
                throw Failure(ex);
            }
        }
    }

    private static MediaDecodeException Failure(Exception inner) => new(
        "Le moteur vidéo libvlc n'a pas pu être chargé. Les fichiers natifs libvlc sont peut-être absents de l'installation.",
        "libvlc-indisponible",
        innerException: inner);
}

/// <summary>
/// Extraction d'une image vidéo sans fenêtre. LibVLC décode la vidéo et livre les pixels par
/// callbacks dans un tampon mémoire ; aucun VideoView ni périphérique audio n'est créé.
/// </summary>
public sealed class VideoFrameGrabber : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private readonly object _gate = new();
    private readonly LibVLC _libVlc;
    private bool _disposed;

    public VideoFrameGrabber()
    {
        VlcRuntime.EnsureInitialized();
        try
        {
            // Le décodage logiciel est imposé : les callbacks mémoire ne reçoivent pas les frames
            // d'un décodeur matériel, et HEVC doit rester lisible sans extension Windows.
            _libVlc = new LibVLC(
                "--no-audio",
                "--no-video-title-show",
                "--avcodec-hw=none",
                "--intf=dummy",
                "--quiet");
        }
        catch (Exception ex)
        {
            throw new MediaDecodeException(
                "Le moteur vidéo libvlc est chargé mais n'a pas pu démarrer.",
                "libvlc-demarrage-impossible",
                innerException: ex);
        }
    }

    /// <summary>Dernier diagnostic lisible, renseigné après chaque tentative infructueuse.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Extrait la première image disponible à <paramref name="position"/>. Retourne <c>null</c> en cas
    /// d'échec et renseigne <see cref="LastError"/>. Les appels sont sérialisés sur cette instance.
    /// </summary>
    public SKBitmap? GrabFrame(string sourcePath, TimeSpan position, TimeSpan? timeout = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            LastError = null;

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                LastError = "Le chemin de la vidéo est vide ; aucune image n'a été extraite.";
                return null;
            }

            if (position < TimeSpan.Zero)
            {
                LastError = "La position demandée ne peut pas être négative.";
                return null;
            }

            TimeSpan wait = timeout ?? DefaultTimeout;
            if (wait <= TimeSpan.Zero)
            {
                LastError = "Le délai d'attente doit être strictement positif.";
                return null;
            }

            if (!File.Exists(sourcePath))
            {
                LastError = $"Le fichier « {sourcePath} » est introuvable ; aucune image n'a été extraite.";
                return null;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(sourcePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                LastError = $"Le chemin de la vidéo « {sourcePath} » est invalide ; aucune image n'a été extraite.";
                return null;
            }

            var sink = new FrameSink();
            VlcMedia? media = null;
            MediaPlayer? player = null;
            EventHandler<EventArgs>? onError = null;
            EventHandler<EventArgs>? onEnd = null;
            string? startFailure = null;

            try
            {
                media = new VlcMedia(_libVlc, fullPath, FromType.FromPath);
                media.AddOption(":no-audio");
                media.AddOption(
                    ":start-time=" +
                    position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));

                player = new MediaPlayer(_libVlc);
                player.Mute = true;

                // Les dimensions de la piste sont connues avant lecture pour les conteneurs locaux.
                // SetVideoFormat fige alors la taille de sortie et évite les reconfigurations du vout
                // qui déformaient légèrement la frame (par exemple 320×180 devenait 320×194).
                (int Width, int Height)? displaySize = TryGetDisplaySize(media);
                if (displaySize is { } size)
                {
                    sink.Configure(size.Width, size.Height);
                    player.SetVideoFormat(
                        "RV32",
                        (uint)size.Width,
                        (uint)size.Height,
                        (uint)size.Width * 4);
                }
                else
                {
                    player.SetVideoFormatCallbacks(sink.FormatCallback, sink.CleanupCallback);
                }

                player.SetVideoCallbacks(sink.LockCallback, sink.UnlockCallback, sink.DisplayCallback);

                onError = (_, _) => sink.Fail("libvlc a signalé une erreur de lecture.");
                onEnd = (_, _) => sink.Fail("La lecture s'est terminée avant qu'une image ne soit disponible.");
                player.EncounteredError += onError;
                player.EndReached += onEnd;

                if (!player.Play(media))
                {
                    startFailure = $"libvlc n'a pas pu démarrer la lecture de « {sourcePath} ».";
                }
                else
                {
                    WaitHandle.WaitAny(
                        new[] { sink.FrameReady.WaitHandle, sink.Failed.WaitHandle },
                        wait);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                startFailure = $"L'extraction de l'image de « {sourcePath} » a échoué : {ex.Message}";
            }
            finally
            {
                if (player is not null)
                {
                    if (onError is not null)
                    {
                        player.EncounteredError -= onError;
                    }

                    if (onEnd is not null)
                    {
                        player.EndReached -= onEnd;
                    }

                    try
                    {
                        player.Stop();
                    }
                    catch (Exception)
                    {
                        // Arrêt best-effort : la libération ci-dessous reste prioritaire.
                    }

                    try
                    {
                        player.Dispose();
                    }
                    catch (Exception)
                    {
                        // LibVLC possède ses propres ressources ; on ne bloque pas le nettoyage du sink.
                    }
                }

                try
                {
                    media?.Dispose();
                }
                catch (Exception)
                {
                }
            }

            SKBitmap? frame;
            string? sinkError;
            lock (sink.Sync)
            {
                frame = sink.Frame;
                sink.Frame = null;
                sinkError = sink.Error;
            }

            sink.Dispose();

            if (frame is not null)
            {
                LastError = null;
                return frame;
            }

            LastError = sinkError ??
                startFailure ??
                $"Aucune image reçue de « {sourcePath} » en {wait.TotalSeconds:0.#} s " +
                $"(position demandée : {position.TotalSeconds:0.##} s). " +
                "Le codec ou le conteneur n'est peut-être pas pris en charge.";
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _libVlc.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VideoFrameGrabber));
        }
    }

    /// <summary>
    /// Dimensions de sortie de la piste vidéo, rotation comprise. VLC applique déjà cette rotation
    /// aux pixels ; on ne l'applique ici qu'aux dimensions, jamais une seconde fois à l'image.
    /// </summary>
    private static (int Width, int Height)? TryGetDisplaySize(VlcMedia media)
    {
        try
        {
            media.Parse(MediaParseOptions.ParseLocal, 5000, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            foreach (MediaTrack track in media.Tracks)
            {
                if (track.TrackType != TrackType.Video)
                {
                    continue;
                }

                uint width = track.Data.Video.Width;
                uint height = track.Data.Video.Height;
                if (width == 0 ||
                    height == 0 ||
                    width > int.MaxValue / 4 ||
                    height > int.MaxValue)
                {
                    continue;
                }

                int rotation = track.Data.Video.Orientation switch
                {
                    VideoOrientation.BottomLeft or VideoOrientation.BottomRight => 180,
                    VideoOrientation.LeftTop or VideoOrientation.RightTop => 90,
                    VideoOrientation.LeftBottom or VideoOrientation.RightBottom => 270,
                    _ => 0,
                };

                DisplayDimensions display = DisplayGeometry.GetDisplaySize(
                    (int)width,
                    (int)height,
                    rotation);
                return (display.Width, display.Height);
            }
        }
        catch (Exception)
        {
            // La piste n'est pas toujours disponible (conteneur exotique, fichier tronqué) :
            // le chemin par callbacks de format reste le repli.
        }

        return null;
    }

    /// <summary>État d'un appel : callbacks natifs, tampon copié et signal d'attente.</summary>
    private sealed class FrameSink : IDisposable
    {
        private static readonly byte[] Chroma = "RV32"u8.ToArray();

        public readonly object Sync = new();
        public readonly ManualResetEventSlim FrameReady = new(false);
        public readonly ManualResetEventSlim Failed = new(false);

        public readonly MediaPlayer.LibVLCVideoFormatCb FormatCallback;
        public readonly MediaPlayer.LibVLCVideoCleanupCb CleanupCallback;
        public readonly MediaPlayer.LibVLCVideoLockCb LockCallback;
        public readonly MediaPlayer.LibVLCVideoUnlockCb UnlockCallback;
        public readonly MediaPlayer.LibVLCVideoDisplayCb DisplayCallback;

        private bool _disposed;
        private bool _outputClosed;

        public FrameSink()
        {
            FormatCallback = OnFormat;
            CleanupCallback = OnCleanup;
            LockCallback = OnLock;
            UnlockCallback = OnUnlock;
            DisplayCallback = OnDisplay;
        }

        public void Configure(int width, int height)
        {
            lock (Sync)
            {
                _outputClosed = false;
                Width = (uint)width;
                Height = (uint)height;
                Pitch = (uint)width * 4;
            }
        }

        public SKBitmap? Frame;
        public string? Error;
        public uint Width;
        public uint Height;
        public uint Pitch;

        private IntPtr _buffer;
        private int _bufferSize;

        private uint OnFormat(
            ref IntPtr opaque,
            IntPtr chroma,
            ref uint width,
            ref uint height,
            ref uint pitches,
            ref uint lines)
        {
            try
            {
                if (width == 0 || height == 0 || width > uint.MaxValue / 4 || height > int.MaxValue)
                {
                    Fail("libvlc a annoncé des dimensions vidéo invalides.");
                    return 0;
                }

                uint pitch = width * 4;
                lock (Sync)
                {
                    if (_disposed)
                    {
                        return 0;
                    }

                    _outputClosed = false;
                    Width = width;
                    Height = height;
                    Pitch = pitch;
                }

                // RV32 est un pixel 32 bits B,G,R,X en mémoire little-endian, directement
                // compatible avec SKColorType.Bgra8888.
                Marshal.Copy(Chroma, 0, chroma, 4);
                pitches = pitch;
                lines = height;
                return 1;
            }
            catch (Exception ex)
            {
                Fail($"La configuration de la sortie vidéo a échoué : {ex.Message}");
                return 0;
            }
        }

        private void OnCleanup(ref IntPtr opaque)
        {
            lock (Sync)
            {
                _outputClosed = true;
                ReleaseBuffer();
            }
        }

        private IntPtr OnLock(IntPtr opaque, IntPtr planes)
        {
            try
            {
                lock (Sync)
                {
                    if (_disposed || _outputClosed || Width == 0 || Height == 0)
                    {
                        return IntPtr.Zero;
                    }

                    long requiredBytes = (long)Width * Height * 4;
                    if (requiredBytes > int.MaxValue)
                    {
                        Fail("La résolution vidéo est trop grande pour le tampon mémoire.");
                        return IntPtr.Zero;
                    }

                    int required = (int)requiredBytes;
                    if (_buffer == IntPtr.Zero || _bufferSize < required)
                    {
                        ReleaseBuffer();
                        _buffer = Marshal.AllocHGlobal(required);
                        _bufferSize = required;
                    }

                    Marshal.WriteIntPtr(planes, _buffer);
                    return _buffer;
                }
            }
            catch (Exception ex)
            {
                Fail($"L'allocation du tampon vidéo a échoué : {ex.Message}");
                return IntPtr.Zero;
            }
        }

        private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
            try
            {
                lock (Sync)
                {
                    if (_disposed || _outputClosed || Frame is not null)
                    {
                        return;
                    }

                    if (Width == 0 || Height == 0 || Pitch < Width * 4)
                    {
                        Fail("La taille du tampon vidéo est incohérente.");
                        return;
                    }

                    IntPtr source = picture != IntPtr.Zero ? picture : Marshal.ReadIntPtr(planes, 0);
                    if (source == IntPtr.Zero)
                    {
                        Fail("libvlc a livré un tampon vidéo vide.");
                        return;
                    }

                    int width = checked((int)Width);
                    int height = checked((int)Height);
                    int pitch = checked((int)Pitch);
                    int rowBytes = checked(width * 4);
                    if (rowBytes > pitch)
                    {
                        Fail("Le pas de ligne annoncé par libvlc est trop petit.");
                        return;
                    }

                    var bitmap = new SKBitmap(new SKImageInfo(
                        width,
                        height,
                        SKColorType.Bgra8888,
                        SKAlphaType.Opaque));
                    try
                    {
                        Span<byte> destination = bitmap.GetPixelSpan();
                        byte[] row = new byte[rowBytes];
                        for (int y = 0; y < height; y++)
                        {
                            Marshal.Copy(IntPtr.Add(source, checked(y * pitch)), row, 0, rowBytes);
                            row.AsSpan(0, rowBytes).CopyTo(destination.Slice(y * bitmap.RowBytes, rowBytes));
                        }

                        Frame = bitmap;
                        FrameReady.Set();
                    }
                    catch
                    {
                        bitmap.Dispose();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Fail($"La copie de l'image vidéo a échoué : {ex.Message}");
            }
        }

        private void OnDisplay(IntPtr opaque, IntPtr picture)
        {
            // La copie est faite dans OnUnlock ; l'affichage n'a rien à faire sans fenêtre.
        }

        public void Fail(string message)
        {
            lock (Sync)
            {
                if (_disposed)
                {
                    return;
                }

                Error ??= message;
                Failed.Set();
            }
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                ReleaseBuffer();
                Frame?.Dispose();
                Frame = null;
                FrameReady.Dispose();
                Failed.Dispose();
            }
        }

        private void ReleaseBuffer()
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = 0;
            }
        }
    }
}
