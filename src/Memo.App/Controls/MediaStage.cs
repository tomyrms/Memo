using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Memo.Media;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using SkiaSharp.Views.Windows;

namespace Memo.App.Controls;

/// <summary>
/// Scène de lecture unifiée : photo ou vidéo, calque optionnel, composition
/// dans le même repère. Les frames vidéo viennent de LibVLC en mémoire, ce qui
/// évite une fenêtre de sortie séparée et garantit l'alignement du calque.
/// </summary>
/// <remarks>
/// Propriété des ressources et règles de concurrence :
/// <list type="bullet">
/// <item>Le thread UI possède <c>_libVlc</c>, <c>_player</c>, <c>_media</c>,
/// <c>_photo</c>, <c>_overlay</c> et le compteur de session. Il est seul à créer,
/// arrêter ou libérer le lecteur ; aucune callback native ne touche ces champs.</item>
/// <item>Le thread de sortie vidéo de libvlc touche <c>_buffer</c>, <c>_frame</c>,
/// <c>_width</c> et <c>_height</c> uniquement sous <c>_frameLock</c>. Le peintre
/// acquiert le même verrou : une frame ne peut donc pas être libérée pendant son
/// dessin, et le tampon natif ne peut pas être remplacé pendant une copie.</item>
/// <item>Le tampon natif n'est pas libéré pendant la vie du lecteur : il est
/// réutilisé tant que sa taille suffit, et les anciens tampons sont retraités
/// (avec une borne) jusqu'à la destruction confirmée du lecteur. Cela évite
/// qu'un vout encore vivant après <c>Stop</c> écrive dans une zone libérée.</item>
/// <item>Les delegates passés à libvlc sont conservés dans des champs. Sans cette
/// racine forte, le GC pourrait collecter le thunk managé alors que libvlc garde
/// encore son pointeur natif.</item>
/// <item>Le tampon natif est un <see cref="SafeHandle"/> : une double libération
/// est impossible et le finaliseur sert de filet de sécurité si le contrôle
/// devient inatteignable sans passer par <c>Unloaded</c>.</item>
/// </list>
/// </remarks>
public sealed class MediaStage : UserControl
{
    private static readonly byte[] ChromaRv32 = "RV32"u8.ToArray();

    private readonly SKXamlCanvas _canvas = new();
    private readonly object _frameLock = new();

    // Racines fortes des callbacks natifs : libvlc conserve un pointeur de fonction
    // vers chacun de ces thunks pendant toute la vie du MediaPlayer.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCallback;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;

    private readonly DispatcherQueue? _dispatcher;

    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private LibVLCSharp.Shared.Media? _media;
    private SKBitmap? _frame;
    private SKBitmap? _photo;
    private SKBitmap? _overlay;
    private NativeVideoBuffer? _buffer;
    private readonly List<NativeVideoBuffer> _retiredBuffers = new();
    private int _width;
    private int _height;
    private bool _overlayVisible = true;
    private bool _mute;
    private int _invalidateQueued;
    private volatile bool _acceptFrames;
    private volatile bool _unloaded;
    private long _playbackId;
    private EventHandler<EventArgs>? _endReachedHandler;

    public event EventHandler? Ended;

    public MediaStage()
    {
        _formatCallback = OnFormat;
        _cleanupCallback = OnCleanup;
        _lockCallback = OnLock;
        _displayCallback = OnDisplay;

        Content = _canvas;
        _canvas.PaintSurface += OnPaintSurface;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool IsVideo => _player is not null && _media is not null;
    public bool Mute
    {
        get => _player?.Mute ?? _mute;
        set
        {
            _mute = value;
            if (_player is not null)
            {
                _player.Mute = value;
            }
        }
    }
    public bool IsPlaying => _player?.IsPlaying == true;
    public long LengthMs => _player?.Length ?? 0;
    public double Position => _player is null || _player.Length <= 0 ? 0 : Math.Clamp((double)_player.Time / _player.Length, 0, 1);

    public void ShowPhoto(string path)
    {
        StopVideo();
        _photo?.Dispose();
        _photo = Decode(path);
        Invalidate();
    }

    public void PlayVideo(string path)
    {
        StopVideo();
        if (_media is not null)
        {
            // StopVideo n'a pas pu arrêter ni libérer le lecteur : ne pas empiler
            // un nouveau média sur un vout dont l'état natif est inconnu.
            return;
        }

        _photo?.Dispose();
        _photo = null;

        EnsurePlayer();
        MediaPlayer? player = _player;
        if (_libVlc is null || player is null)
        {
            return;
        }

        LibVLCSharp.Shared.Media? media = null;
        try
        {
            media = new LibVLCSharp.Shared.Media(_libVlc, new Uri(path));
            _media = media;
            _acceptFrames = true;
            long session = Interlocked.Increment(ref _playbackId);
            DetachEndHandler(player);
            var handler = new EventHandler<EventArgs>((_, _) => OnEndReached(session));
            _endReachedHandler = handler;
            player.EndReached += handler;

            if (!player.Play(media))
            {
                // Une relance immédiate après Stop peut échouer si la sortie
                // précédente n'a pas fini de se fermer : on rend l'état propre.
                _acceptFrames = false;
                DetachEndHandler(player);
                try
                {
                    player.Stop();
                }
                catch
                {
                    // Le lecteur peut déjà être arrêté ou libéré.
                }

                if (ReferenceEquals(_media, media))
                {
                    _media = null;
                }

                media.Dispose();
            }
        }
        catch
        {
            _acceptFrames = false;
            DetachEndHandler(player);
            try
            {
                player.Stop();
            }
            catch
            {
                // Arrêt best-effort ; la libération du média reste prioritaire.
            }

            if (ReferenceEquals(_media, media))
            {
                _media = null;
            }

            media?.Dispose();
        }
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
        // Fermer l'acceptation avant l'arrêt : même si libvlc signale une fin
        // pendant Stop, l'événement ne sera pas propagé à l'interface.
        _acceptFrames = false;
        Interlocked.Increment(ref _playbackId);

        MediaPlayer? player = _player;
        bool nativeSafe = true;
        if (player is not null)
        {
            DetachEndHandler(player);
            try
            {
                // Stop est appelé même si IsPlaying est faux : il remet le lecteur
                // dans un état propre après une fin naturelle ou une erreur.
                player.Stop();
            }
            catch
            {
                // Si Stop échoue, libérer complètement le lecteur : son thread de
                // sortie pourrait sinon écrire encore dans le tampon natif.
                try
                {
                    player.Dispose();
                    _player = null;
                }
                catch
                {
                    // Ni Stop ni Dispose n'ont abouti : ne pas libérer le tampon
                    // sous un vout potentiellement vivant. Il sera retenté plus tard.
                    nativeSafe = false;
                }
            }
        }

        if (!nativeSafe)
        {
            return;
        }

        LibVLCSharp.Shared.Media? media = _media;
        _media = null;
        try
        {
            media?.Dispose();
        }
        catch
        {
            // La libération du tampon ci-dessous ne dépend pas du média.
        }

        lock (_frameLock)
        {
            _frame?.Dispose();
            _frame = null;
            // _width/_height décrivent le tampon conservé : les callbacks
            // OnLock/OnFormat restent valides pendant la fermeture du vout.
            // Le tampon natif est conservé : libvlc peut encore le référencer
            // quelques instants après Stop. Il est réutilisé au prochain format
            // et libéré seulement après la destruction confirmée du lecteur.
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

        LibVLC? libVlc = _libVlc;
        bool createdLibVlc = false;
        MediaPlayer? player = null;
        try
        {
            if (libVlc is null)
            {
                libVlc = new LibVLC("--no-video-title-show", "--quiet", "--avcodec-hw=none");
                createdLibVlc = true;
            }

            player = new MediaPlayer(libVlc) { Volume = 100, Mute = _mute };
            player.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
            player.SetVideoCallbacks(_lockCallback, null, _displayCallback);

            _libVlc = libVlc;
            _player = player;
        }
        catch
        {
            if (player is not null)
            {
                DetachEndHandler(player);
                try
                {
                    player.Dispose();
                }
                catch
                {
                    // Libération best-effort avant de propager l'échec d'initialisation.
                }
            }

            if (createdLibVlc)
            {
                try
                {
                    libVlc?.Dispose();
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // RV32 = BGRA sur little-endian, format natif des callbacks LibVLC.
        try
        {
            if (width == 0 ||
                height == 0 ||
                width > int.MaxValue / 4 ||
                height > int.MaxValue)
            {
                return 0;
            }

            long totalBytes = (long)width * height * 4;
            if (totalBytes <= 0 || totalBytes > int.MaxValue)
            {
                return 0;
            }

            int frameWidth = (int)width;
            int frameHeight = (int)height;
            int bufferSize = (int)totalBytes;

            NativeVideoBuffer? candidateBuffer = null;
            SKBitmap? candidateFrame = null;
            try
            {
                lock (_frameLock)
                {
                    bool accepting = _acceptFrames;
                    if (_buffer is null || _buffer.IsInvalid || _buffer.Size < bufferSize)
                    {
                        if (_buffer is not null &&
                            !_buffer.IsInvalid &&
                            _retiredBuffers.Count >= 8)
                        {
                            // Cas pathologique de résolutions strictement croissantes :
                            // borner la rétention en libérant le plus ancien tampon,
                            // plusieurs générations de vout derrière nous.
                            NativeVideoBuffer oldest = _retiredBuffers[0];
                            _retiredBuffers.RemoveAt(0);
                            oldest.Dispose();
                        }

                        candidateBuffer = new NativeVideoBuffer(bufferSize);
                    }

                    if (accepting &&
                        (_frame is null || _frame.Width != frameWidth || _frame.Height != frameHeight))
                    {
                        candidateFrame = new SKBitmap(new SKImageInfo(
                            frameWidth,
                            frameHeight,
                            SKColorType.Bgra8888,
                            SKAlphaType.Opaque));
                    }

                    if (candidateBuffer is { IsInvalid: true } ||
                        (candidateFrame is not null && candidateFrame.GetPixels() == IntPtr.Zero))
                    {
                        candidateBuffer?.Dispose();
                        candidateBuffer = null;
                        candidateFrame?.Dispose();
                        candidateFrame = null;
                        return 0;
                    }

                    if (candidateBuffer is not null)
                    {
                        NativeVideoBuffer? previous = _buffer;
                        _buffer = candidateBuffer;
                        candidateBuffer = null;
                        if (previous is not null)
                        {
                            if (previous.IsInvalid)
                            {
                                previous.Dispose();
                            }
                            else
                            {
                                // Ne pas libérer sous un vout potentiellement encore
                                // vivant : le tampon part à la retraite et sera
                                // libéré avec le lecteur.
                                _retiredBuffers.Add(previous);
                            }
                        }
                    }

                    if (candidateFrame is not null)
                    {
                        SKBitmap? previous = _frame;
                        _frame = candidateFrame;
                        candidateFrame = null;
                        previous?.Dispose();
                    }

                    _width = frameWidth;
                    _height = frameHeight;
                }
            }
            catch
            {
                candidateBuffer?.Dispose();
                candidateFrame?.Dispose();
                return 0;
            }

            Marshal.Copy(ChromaRv32, 0, chroma, ChromaRv32.Length);
            pitches = (uint)frameWidth * 4;
            lines = (uint)frameHeight;
            return 1;
        }
        catch
        {
            // Une callback native ne doit jamais laisser échapper d'exception.
            return 0;
        }
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        // Le tampon natif n'est pas libéré ici : une cleanup tardive d'une ancienne
        // sortie pourrait sinon libérer le tampon déjà réinstallé par un nouveau
        // PlayVideo. Il est conservé pour le prochain format et libéré par
        // DisposePlayer après destruction du lecteur. La dernière SKBitmap est
        // conservée jusqu'au prochain média pour ne pas effacer l'image.
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        try
        {
            lock (_frameLock)
            {
                if (_buffer is null ||
                    _buffer.IsInvalid ||
                    _width <= 0 ||
                    _height <= 0)
                {
                    return IntPtr.Zero;
                }

                IntPtr pointer = _buffer.DangerousGetHandle();
                if (planes != IntPtr.Zero)
                {
                    Marshal.WriteIntPtr(planes, 0, pointer);
                }

                return pointer;
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        bool copied = false;
        try
        {
            lock (_frameLock)
            {
                if (!_acceptFrames || _buffer is null || _buffer.IsInvalid)
                {
                    return;
                }

                SKBitmap? frame = _frame;
                if (frame is null || frame.Width != _width || frame.Height != _height)
                {
                    return;
                }

                IntPtr destination = frame.GetPixels();
                if (destination == IntPtr.Zero)
                {
                    return;
                }

                int sourceStride = _width * 4;
                int destinationStride = frame.RowBytes;
                long required = (long)sourceStride * _height;
                if (destinationStride < sourceStride ||
                    required <= 0 ||
                    required > _buffer.Size)
                {
                    return;
                }

                unsafe
                {
                    byte* source = (byte*)_buffer.DangerousGetHandle();
                    byte* target = (byte*)destination;
                    if (sourceStride == destinationStride)
                    {
                        Buffer.MemoryCopy(source, target, required, required);
                    }
                    else
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            Buffer.MemoryCopy(
                                source + ((long)y * sourceStride),
                                target + ((long)y * destinationStride),
                                destinationStride,
                                sourceStride);
                        }
                    }
                }

                copied = true;
            }
        }
        catch
        {
            // Une callback native ne doit jamais laisser échapper d'exception.
            return;
        }

        if (copied)
        {
            QueueInvalidate();
        }
    }

    private void QueueInvalidate()
    {
        if (_unloaded || _dispatcher is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _invalidateQueued, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_dispatcher.TryEnqueue(() =>
                {
                    Interlocked.Exchange(ref _invalidateQueued, 0);
                    if (_unloaded)
                    {
                        return;
                    }

                    try
                    {
                        _canvas.Invalidate();
                    }
                    catch
                    {
                        // La surface peut être détruite pendant la fermeture de la fenêtre.
                    }
                }))
            {
                Interlocked.Exchange(ref _invalidateQueued, 0);
            }
        }
        catch
        {
            // La file du dispatcher peut être fermée pendant l'arrêt de l'application.
            Interlocked.Exchange(ref _invalidateQueued, 0);
        }
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x0E, 0x0D, 0x0C));

        using var paint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        // Le verrou couvre le choix et le dessin de la frame vidéo : OnFormat et
        // StopVideo ne peuvent pas disposer la bitmap pendant DrawBitmap.
        lock (_frameLock)
        {
            var source = _frame;
            if (source is null || source.Width == 0 || source.Height == 0)
            {
                source = _photo;
            }

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

            if (_overlayVisible &&
                _overlay is { Width: > 0, Height: > 0 } overlay)
            {
                canvas.DrawBitmap(overlay, dest, sampling, paint);
            }
        }
    }

    private void Invalidate()
    {
        if (_unloaded)
        {
            return;
        }

        try
        {
            _canvas.Invalidate();
        }
        catch
        {
            // La surface peut être détruite pendant la fermeture de la fenêtre.
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _unloaded = false;
        Invalidate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _unloaded = true;
        DisposePlayer();
    }

    private void OnEndReached(long session)
    {
        if (!_acceptFrames || session != Interlocked.Read(ref _playbackId))
        {
            return;
        }

        // Capture de la session : un Stop ou un nouveau PlayVideo invalide
        // l'événement avant qu'il ne soit traité par la file UI.
        try
        {
            _dispatcher?.TryEnqueue(() =>
            {
                if (_unloaded || session != Interlocked.Read(ref _playbackId))
                {
                    return;
                }

                Ended?.Invoke(this, EventArgs.Empty);
            });
        }
        catch
        {
            // La file du dispatcher peut être fermée pendant l'arrêt de l'application.
        }
    }

    private void DetachEndHandler(MediaPlayer? player)
    {
        EventHandler<EventArgs>? handler = _endReachedHandler;
        _endReachedHandler = null;
        if (player is not null && handler is not null)
        {
            try
            {
                player.EndReached -= handler;
            }
            catch
            {
                // Le lecteur peut déjà être libéré.
            }
        }
    }

    private static SKBitmap? Decode(string path)
    {
        try
        {
            using var decoded = ImageDecoder.Decode(path);
            return decoded.Bitmap.Copy();
        }
        catch
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
    }

    private void DisposePlayer()
    {
        _acceptFrames = false;
        Interlocked.Increment(ref _playbackId);

        MediaPlayer? player = _player;
        bool playerReleased = true;
        if (player is not null)
        {
            DetachEndHandler(player);
            try
            {
                player.Stop();
            }
            catch
            {
                // Le lecteur peut déjà être arrêté.
            }

            try
            {
                player.Dispose();
                _player = null;
            }
            catch
            {
                // Conserver la référence : libérer le tampon sous un lecteur
                // encore vivant provoquerait exactement l'use-after-free évité ici.
                playerReleased = false;
            }
        }

        if (playerReleased)
        {
            LibVLCSharp.Shared.Media? media = _media;
            _media = null;
            try
            {
                media?.Dispose();
            }
            catch
            {
            }

            LibVLC? libVlc = _libVlc;
            _libVlc = null;
            try
            {
                libVlc?.Dispose();
            }
            catch
            {
            }

            lock (_frameLock)
            {
                _frame?.Dispose();
                _frame = null;
                _buffer?.Dispose();
                _buffer = null;
                foreach (NativeVideoBuffer retired in _retiredBuffers)
                {
                    retired.Dispose();
                }

                _retiredBuffers.Clear();
                _width = 0;
                _height = 0;
            }
        }

        _photo?.Dispose();
        _photo = null;
        _overlay?.Dispose();
        _overlay = null;
        Interlocked.Exchange(ref _invalidateQueued, 0);
    }

    /// <summary>
    /// Tampon natif alloué pour une sortie vidéo. Le pointeur est publié à libvlc
    /// entre <c>OnLock</c> et <c>OnDisplay</c>. Il n'est libéré qu'après la
    /// destruction confirmée du lecteur ; les buffers remplacés restent dans la
    /// liste des retraités jusque-là (avec éviction du plus ancien au-delà de huit).
    /// </summary>
    private sealed class NativeVideoBuffer : SafeHandle
    {
        public NativeVideoBuffer(int size)
            : base(IntPtr.Zero, ownsHandle: true)
        {
            Size = size;
            SetHandle(Marshal.AllocHGlobal(size));
        }

        public int Size { get; }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }
}
