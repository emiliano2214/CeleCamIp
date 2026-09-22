using System.Diagnostics;
using System.Runtime.InteropServices;
using Concentus.Enums;
using Concentus.Structs;

namespace CeleCamIp.Gateway.Services;

/// <summary>
/// Fuente de audio que usa FFmpeg como proceso externo (igual criterio que
/// FFmpegProcessSource para video: invocar el binario en vez de un binding
/// P/Invoke) para decodificar el audio RTSP de la camara a PCM crudo, y
/// codifica ese PCM a Opus en C# usando Concentus (puerto managed de libopus,
/// sin dependencias nativas adicionales - importante porque el Gateway ya
/// depende de las DLLs nativas de FFmpeg para el video, no queremos sumar
/// otra dependencia nativa mas para audio).
///
/// POR QUE NO SE LE PIDE OPUS DIRECTO A FFMPEG:
/// FFmpeg puede codificar a libopus el, pero la salida por pipe necesita ir
/// en algun contenedor que delimite los paquetes Opus (tipicamente Ogg), y
/// habria que escribir un demuxer de Ogg a mano ademas del encoder. Pidiendo
/// PCM crudo (-f s16le) el framing es trivial (bytes por muestra fijos) y el
/// encoder Opus se corre nosotros mismos en frames de tamano exacto (20ms),
/// que es lo que Opus/WebRTC espera de todas formas.
///
/// POR QUE UN PROCESO FFMPEG SEPARADO DEL DE VIDEO (no un solo -map 0:v:0 -map 0:a:0):
/// Mezclar video H264 crudo y audio PCM en un mismo pipe stdout requeriria un
/// contenedor (los dos "-f" no pueden coexistir sueltos en un pipe:1) y un
/// demuxer de ese contenedor del lado C#. Dos procesos FFmpeg independientes
/// (uno -map 0:v:0 -an, otro -map 0:a:0 -vn) evitan esa complejidad a costa
/// de decodificar el RTSP dos veces (aceptable: el Gateway esta en la LAN de
/// la camara, no hay costo de ancho de banda WAN, y el CPU de decodificar
/// solo el canal de audio es minimo).
///
/// CLOCK RATE: Opus en RTP/WebRTC SIEMPRE usa clock rate 48000, sin importar
/// el sample rate real que use el encoder internamente (RFC 7587). Por eso
/// se resamplea todo a 48000 antes de codificar, y "durationRtpUnits" de
/// cada frame es directamente la cantidad de muestras @48kHz del frame
/// (960 para 20ms), no algo que haya que calcular con tiempo real transcurrido
/// como en el video (ahi FFmpeg entrega frames a ritmo variable; aca el PCM
/// llega a un bitrate constante y cada frame Opus dura siempre lo mismo).
/// </summary>
public class AudioProcessSource : IDisposable
{
    private const int SampleRateHz = 48000;
    private const int Channels = 1; // La mayoria de camaras IP tienen microfono mono; Opus/SDP se anuncia igual como "opus/48000/2" por convencion RFC 7587, sin relacion con el canal real codificado.
    private const int FrameDurationMs = 20;
    private const int SamplesPerFrame = SampleRateHz * FrameDurationMs / 1000; // 960
    private const int BytesPerSample = 2; // s16le
    private const int BytesPerFrame = SamplesPerFrame * BytesPerSample * Channels; // 1920 bytes (mono)
    private const int InitialPendingBufferSize = 1 << 16; // 64 KB, el audio pesa mucho menos que el video.
    private const int MaxOpusPacketSize = 4000; // Cota generosa; un frame Opus real casi nunca supera ~500 bytes.

    private Process? _process;
    private readonly string _rtspUrl;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private readonly ILogger? _logger;
    private readonly object _lock = new object();
    private bool _isRunning;
    private Stream? _outputStream;
    private OpusEncoder? _encoder;
    private int _frameCount;
    private readonly DateTime _startTime;

    private byte[] _pendingBuffer = new byte[InitialPendingBufferSize];
    private int _pendingStart;
    private int _pendingEnd;

    public event Action<uint, byte[]>? OnAudioSourceEncodedSample;
    public event Action<string>? OnAudioSourceError;

    public AudioProcessSource(string rtspUrl, ILogger? logger = null)
    {
        _rtspUrl = rtspUrl;
        _logger = logger;
        _startTime = DateTime.UtcNow;
    }

    public async Task StartAudio()
    {
        _logger?.LogInformation($"[AudioProcess] Iniciando FFmpeg (audio) para: {_rtspUrl}");
        Console.WriteLine($"[AudioProcess] Iniciando para: {_rtspUrl}");

        try
        {
            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                throw new FileNotFoundException("No se encontro ffmpeg.exe en el PATH");
            }

            _encoder = new OpusEncoder(SampleRateHz, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = 24000; // Voz/audio ambiente de camara IP, no hace falta mas.

            // Mismos valores de -probesize/-analyzeduration/-timeout/-err_detect
            // que el pipeline de video (ver FFmpegProcessSource) por la misma
            // razon: dar margen a camaras que mandan parametros in-band, sin
            // sumar latencia de arranque innecesaria. -map 0:a:0 -vn: solo
            // audio, nada de video en este proceso (lo maneja FFmpegProcessSource
            // por separado). Si la camara NO tiene stream de audio, -map 0:a:0
            // hace que FFmpeg falle rapido y en forma explicita ("Stream map
            // '0:a:0' matches no streams"): eso se loguea y se trata como no
            // fatal desde WebRtcCameraSession (la sesion sigue con video solo).
            var args = $"-fflags nobuffer -flags low_delay -probesize 65536 -analyzeduration 300000 " +
                      $"-timeout 10000000 " +
                      $"-err_detect ignore_err -rtsp_transport udp -i \"{_rtspUrl}\" " +
                      $"-map 0:a:0 -vn -ac {Channels} -ar {SampleRateHz} -acodec pcm_s16le -f s16le pipe:1";

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = new Process { StartInfo = startInfo };
            _process.ErrorDataReceived += OnErrorDataReceived;
            _process.Exited += OnProcessExited;

            _process.Start();
            _outputStream = _process.StandardOutput.BaseStream;
            _process.BeginErrorReadLine();

            _isRunning = true;
            _logger?.LogInformation("[AudioProcess] FFmpeg (audio) iniciado correctamente");
            Console.WriteLine("[AudioProcess] FFmpeg (audio) iniciado correctamente");

            _ = Task.Run(() => ReadPcmAsync(_cts.Token));
            _ = MonitorProcessAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[AudioProcess] Error iniciando FFmpeg (audio): {ex.Message}");
            Console.WriteLine($"[AudioProcess] Error: {ex.Message}");
            OnAudioSourceError?.Invoke(ex.Message);
            throw;
        }
    }

    private string FindFFmpeg()
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();

        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path, "ffmpeg.exe");
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        if (File.Exists("ffmpeg.exe"))
        {
            return "ffmpeg.exe";
        }

        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var appPath = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(appPath))
        {
            return appPath;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (File.Exists("/usr/bin/ffmpeg"))
            {
                return "/usr/bin/ffmpeg";
            }
            if (File.Exists("/usr/local/bin/ffmpeg"))
            {
                return "/usr/local/bin/ffmpeg";
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Lee el pipe de FFmpeg (PCM s16le crudo, sin ningun framing propio: a
    /// diferencia del H264 Annex-B del video, aca cada muestra son
    /// exactamente 2 bytes seguidos, sin start codes que buscar) y va
    /// extrayendo frames de tamano fijo (BytesPerFrame) a medida que hay
    /// suficientes bytes acumulados.
    /// </summary>
    private async Task ReadPcmAsync(CancellationToken cancellationToken)
    {
        if (_outputStream == null) return;

        var readChunk = new byte[16384];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                int bytesRead = await _outputStream.ReadAsync(readChunk, 0, readChunk.Length, cancellationToken);
                if (bytesRead == 0) break; // FFmpeg cerro stdout

                lock (_lock)
                {
                    AppendToPendingBuffer(readChunk, bytesRead);
                    ProcessPendingBuffer();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelacion normal
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[AudioProcess] Error leyendo salida de FFmpeg: {ex.Message}");
            OnAudioSourceError?.Invoke($"Error leyendo salida de audio: {ex.Message}");
        }
    }

    private void AppendToPendingBuffer(byte[] chunk, int count)
    {
        if (_pendingEnd + count > _pendingBuffer.Length)
        {
            int usedLength = _pendingEnd - _pendingStart;
            if (_pendingStart > 0)
            {
                Buffer.BlockCopy(_pendingBuffer, _pendingStart, _pendingBuffer, 0, usedLength);
                _pendingStart = 0;
                _pendingEnd = usedLength;
            }

            if (_pendingEnd + count > _pendingBuffer.Length)
            {
                int newSize = Math.Max(_pendingBuffer.Length * 2, _pendingEnd + count);
                Array.Resize(ref _pendingBuffer, newSize);
            }
        }

        Buffer.BlockCopy(chunk, 0, _pendingBuffer, _pendingEnd, count);
        _pendingEnd += count;
    }

    /// <summary>
    /// Mientras haya al menos un frame completo (BytesPerFrame) disponible,
    /// lo convierte a short[] PCM, lo codifica a Opus y dispara el evento.
    /// Debe llamarse con _lock tomado.
    /// </summary>
    private void ProcessPendingBuffer()
    {
        if (_encoder == null) return;

        var pcmSamples = new short[SamplesPerFrame * Channels];
        var opusBuffer = new byte[MaxOpusPacketSize];

        while (_pendingEnd - _pendingStart >= BytesPerFrame)
        {
            for (int i = 0; i < pcmSamples.Length; i++)
            {
                int byteIndex = _pendingStart + i * BytesPerSample;
                // s16le: little-endian, byte bajo primero.
                pcmSamples[i] = (short)(_pendingBuffer[byteIndex] | (_pendingBuffer[byteIndex + 1] << 8));
            }
            _pendingStart += BytesPerFrame;

            try
            {
                int encodedLength = _encoder.Encode(pcmSamples, 0, SamplesPerFrame, opusBuffer, 0, opusBuffer.Length);
                var opusPacket = new byte[encodedLength];
                Buffer.BlockCopy(opusBuffer, 0, opusPacket, 0, encodedLength);

                _frameCount++;
                if (_frameCount == 1)
                {
                    var elapsed = DateTime.UtcNow - _startTime;
                    Console.WriteLine($"[AudioProcess] 🔊 PRIMER FRAME DE AUDIO: {elapsed.TotalMilliseconds:F0}ms, {encodedLength} bytes Opus");
                }
                if (_frameCount % 100 == 0)
                {
                    Console.WriteLine($"[AudioProcess] 🔊 Frames de audio enviados: {_frameCount}");
                }

                OnAudioSourceEncodedSample?.Invoke(SamplesPerFrame, opusPacket);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[AudioProcess] Error codificando Opus: {ex.Message}");
                OnAudioSourceError?.Invoke($"Error codificando Opus: {ex.Message}");
            }
        }

        // Compactar si ya no queda espacio libre al final (igual criterio que en FFmpegProcessSource).
        if (_pendingStart > 0 && _pendingEnd == _pendingBuffer.Length)
        {
            int usedLength = _pendingEnd - _pendingStart;
            Buffer.BlockCopy(_pendingBuffer, _pendingStart, _pendingBuffer, 0, usedLength);
            _pendingStart = 0;
            _pendingEnd = usedLength;
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            Console.WriteLine($"[AudioProcess] {e.Data}");

            if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("matches no streams", StringComparison.OrdinalIgnoreCase))
            {
                OnAudioSourceError?.Invoke(e.Data);
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _isRunning = false;

        _logger?.LogWarning($"[AudioProcess] FFmpeg (audio) termino con codigo: {exitCode}");
        Console.WriteLine($"[AudioProcess] FFmpeg (audio) termino con codigo: {exitCode}");

        if (exitCode != 0 && !_cts.IsCancellationRequested)
        {
            OnAudioSourceError?.Invoke($"FFmpeg (audio) termino inesperadamente con codigo: {exitCode} (¿la camara no tiene stream de audio?)");
        }
    }

    private async Task MonitorProcessAsync()
    {
        while (!_cts.IsCancellationRequested && _isRunning)
        {
            await Task.Delay(1000, _cts.Token);

            if (_process != null && _process.HasExited)
            {
                _isRunning = false;
                break;
            }
        }
    }

    public async Task CloseAudio()
    {
        try
        {
            _cts.Cancel();

            if (_process != null && !_process.HasExited)
            {
                _process.Kill();
                var exitTask = _process.WaitForExitAsync();
                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(exitTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _logger?.LogWarning("[AudioProcess] Timeout esperando que FFmpeg (audio) termine");
                }
            }

            _isRunning = false;
            _logger?.LogInformation("[AudioProcess] FFmpeg (audio) detenido");
            Console.WriteLine("[AudioProcess] FFmpeg (audio) detenido");
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[AudioProcess] Error deteniendo FFmpeg (audio): {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _process?.Dispose();
        _cts.Dispose();
        _outputStream?.Dispose();

        lock (_lock)
        {
            _pendingStart = 0;
            _pendingEnd = 0;
        }

        Console.WriteLine("[AudioProcess] ✅ Disposed");
    }
}
