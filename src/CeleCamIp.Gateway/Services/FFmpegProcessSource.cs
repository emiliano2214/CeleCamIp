using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CeleCamIp.Gateway.Services;

/// <summary>
/// Fuente de video que usa FFmpeg como proceso externo en lugar de SIPSorceryMedia.FFmpeg.
/// Esto evita el bug de decodificación de SIPSorceryMedia.FFmpeg.
///
/// HISTORIAL DE BUGS ARREGLADOS EN ESTA VERSION:
///
/// 1) El buffer de lectura se reseteaba en CADA ReadAsync del pipe de FFmpeg
///    (frameBuffer.SetLength(0) despues de cada lectura). Un NAL de un frame
///    1080p (cientos de KB) casi nunca entra en un solo read de 64KB, asi que
///    cualquier NAL cortado a mitad de camino se emitia como "completo" con
///    el pedazo que habia hasta ese momento (a veces de 1 solo byte, como se
///    vio en produccion). Fix: _pendingBuffer es persistente entre lecturas;
///    solo se descarta lo que ya se confirmo como un NAL completo (delimitado
///    por el SIGUIENTE start code), lo demas se queda esperando mas datos.
///
/// 2) Cada NAL individual (SPS, PPS, slice) se mandaba como un "frame"
///    separado con durationRtpUnits=0 fijo. Eso deja el timestamp RTP
///    congelado para siempre, y el navegador nunca puede reconstruir la
///    linea de tiempo del video (se queda "conectado" pero nunca decodifica
///    nada). Fix: se agrupan los NALs de un mismo frame (non-VCL: SPS/PPS/SEI
///    + el NAL de slice que cierra el access unit) y se emiten juntos como
///    un solo "sample", con una duracion RTP calculada por el tiempo real
///    transcurrido entre frames (90000 Hz es el clock rate estandar de video
///    en RTP/WebRTC).
///
/// 3) [LATENCIA] El buffer de lectura del pipe era un List&lt;byte&gt; al que se
///    agregaba byte por byte (_pendingBuffer.Add(readChunk[i]) hasta 65536
///    veces por lectura) y del que se removian NALs con RemoveRange, que hace
///    un Array.Copy de todo el resto del buffer en cada NAL extraido. Con
///    FFmpeg entregando decenas de lecturas por segundo, esto consumia mas
///    CPU de la que el pipe podia vaciarse, el buffer del SO se llenaba,
///    FFmpeg se bloqueaba escribiendo, y la latencia crecia con el tiempo de
///    reproduccion (sintoma: speed=0.5x/0.7x en el log de FFmpeg pese a usar
///    -c:v copy, que no deberia ir lento). Fix: _pendingBuffer pasa a ser un
///    byte[] contiguo con punteros _pendingStart/_pendingEnd. Agregar datos
///    es un solo Buffer.BlockCopy por lectura (no un loop). Consumir un NAL
///    ya procesado es O(1) (solo avanza _pendingStart); el buffer solo se
///    compacta (memmove de lo que falta procesar) cuando no queda espacio
///    libre al final para el proximo append, no en cada NAL extraido.
///
/// 4) [LATENCIA DE ARRANQUE] FFmpeg por defecto analiza el stream de entrada
///    (probesize/analyzeduration) antes de largar salida, sumando 5-7s antes
///    del primer frame. Para un pass-through RTSP donde el codec ya se conoce
///    por el SDP (DESCRIBE), ese analisis es innecesario. Fix: se agregan
///    -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0
///    antes del -i para arrancar con el primer paquete disponible.
///
/// 5) [CRASH SILENCIOSO - NUNCA HAY VIDEO] El fix (4) resulto DEMASIADO
///    agresivo: -probesize 32 -analyzeduration 0 solo funciona si la camara
///    manda los parametros H264 (SPS/PPS) en el SDP de la respuesta RTSP
///    DESCRIBE (campo sprop-parameter-sets). Camaras que los mandan in-band
///    (dentro del propio stream, como es comun en camaras IP baratas) hacen
///    que FFmpeg no llegue a ver ni el primer NAL con 32 bytes de probing,
///    no pueda determinar el tamano del video ("unspecified size"), descarte
///    el stream de video de la salida, y el proceso muera con codigo -22
///    (EINVAL) medio segundo despues de arrancar, sin ningun timeout de por
///    medio y sin haber mandado un solo frame (sintoma: en el log de
///    diagnostico se ve "FFmpeg arranco correctamente" seguido casi de
///    inmediato por "FFmpeg termino con codigo: -22", con la sesion WebRTC
///    llegando a "connected" igual porque el problema es 100% del lado de
///    FFmpeg, no de ICE). Fix: probesize/analyzeduration moderados (64KB /
///    0.3s) en vez de casi 0 - siguen siendo ordenes de magnitud mas chicos
///    que el default (5MB / 5s) pero le dan a FFmpeg margen real para
///    encontrar el SPS/PPS in-band, mas -map 0:v:0 explicito para no
///    depender de que la deteccion automatica de stream "adivine" bien.
///
/// 6) [SIN TIMEOUT DE CONEXION RTSP] Sin timeout explicito, si la camara no
///    responde (apagada, colgada, IP incorrecta, cambio de credenciales,
///    etc) el proceso ffmpeg queda esperando indefinidamente en el handshake
///    RTSP (DESCRIBE/SETUP/PLAY, que siempre va por TCP aunque el video
///    despues vaya por UDP con -rtsp_transport udp). MonitorProcessAsync()
///    solo detecta cuando el proceso YA termino, no puede hacer nada si el
///    proceso nunca llega a terminar solo. Fix: -timeout (propio del
///    demuxer RTSP, cubre el canal de control TCP) y -rw_timeout (generico
///    de libavformat, cubre cualquier operacion de red, incluido el canal
///    de datos UDP) en 10s (10000000 microsegundos) - generoso para una
///    camara en la misma LAN que el Gateway, sin llegar a ser un timeout
///    "eterno". OJO: esto es el timeout de la conexion RTSP Gateway<->camara
///    (LAN), totalmente independiente del timeout/latencia de ICE/TURN
///    (Gateway<->viewer, WAN) - no tienen relacion entre si, y FFmpeg ya
///    arranca en paralelo a la negociacion ICE (ver WebRtcCameraSession.cs),
///    asi que este timeout no puede interferir con el cierre de la sesion
///    WebRTC por ICE failed/disconnected.
/// </summary>
public class FFmpegProcessSource : IDisposable
{
    private const int RtpClockRate = 90000;
    private const int DefaultAssumedFps = 25; // Fallback solo para el primer frame, antes de tener una medicion real.
    private const int InitialPendingBufferSize = 1 << 20; // 1 MB inicial, crece si hace falta.

    private Process? _process;
    private readonly string _rtspUrl;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private readonly ILogger? _logger;
    private readonly object _lock = new object();
    private int _frameCount;
    private readonly DateTime _startTime;
    private bool _isRunning;
    private Stream? _outputStream;

    /// <summary>
    /// Buffer contiguo de bytes acumulados entre lecturas del pipe que
    /// todavia no forman un NAL completo confirmado. Los bytes validos son
    /// el rango [_pendingStart, _pendingEnd). Consumir bytes ya procesados
    /// es O(1) (avanzar _pendingStart); solo se compacta con memmove cuando
    /// no hay espacio libre al final para el proximo append.
    /// </summary>
    private byte[] _pendingBuffer = new byte[InitialPendingBufferSize];
    private int _pendingStart;
    private int _pendingEnd;

    /// <summary>NALs ya extraidos que pertenecen al access unit (frame) actual, esperando el NAL de slice que lo cierra.</summary>
    private readonly List<byte> _accessUnitBuffer = new();

    private DateTime? _lastAccessUnitTime;

    public event Action<uint, byte[]>? OnVideoSourceEncodedSample;
    public event Action<string>? OnVideoSourceError;

    public FFmpegProcessSource(string rtspUrl, ILogger? logger = null)
    {
        _rtspUrl = rtspUrl;
        _logger = logger;
        _startTime = DateTime.UtcNow;
    }

    public async Task StartVideo()
    {
        _logger?.LogInformation($"Iniciando FFmpeg externo para: {_rtspUrl}");
        Console.WriteLine($"[FFmpegProcess] Iniciando para: {_rtspUrl}");

        try
        {
            var ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                throw new FileNotFoundException("No se encontró ffmpeg.exe en el PATH");
            }

            _logger?.LogInformation($"FFmpeg encontrado en: {ffmpegPath}");

            // [FIX 5 - "no veo video" pese a que FFmpeg arranca OK] La combinacion
            // -probesize 32 -analyzeduration 0 SOLO funciona si la camara manda los
            // parametros H264 (SPS/PPS) en el propio SDP de la respuesta RTSP DESCRIBE
            // (campo sprop-parameter-sets). Muchas camaras IP baratas NO lo hacen: los
            // mandan in-band, como parte del propio stream de video. Con 32 bytes de
            // probing, FFmpeg ni siquiera llega a ver el primer NAL, no puede determinar
            // el tamano del video ("unspecified size"), descarta el stream de video de
            // la salida por completo, y el muxer de pipe:1 termina sin ningun stream
            // mapeado -> proceso muere con codigo -22 (EINVAL) antes de mandar un solo
            // frame, sin ningun timeout de por medio: FFmpeg arranca "bien" y crashea
            // solo, en silencio, medio segundo despues.
            //
            // Fix: probesize/analyzeduration moderados (64KB / 0.3s) en vez de casi 0.
            // Siguen siendo MUCHISIMO mas chicos que el default de FFmpeg (5MB / 5s,
            // que es de donde salian los 5-7s de latencia de arranque originales), pero
            // le dan a FFmpeg margen real para encontrar el primer SPS/PPS in-band.
            // Se agrega ademas -map 0:v:0 explicito para forzar el mapeo del stream de
            // video aunque la deteccion automatica dude, y -err_detect ignore_err para
            // no abortar por errores menores de bitstream (frecuentes en camaras baratas
            // con paquetes RTP perdidos) en vez de cortar la transmision entera.
            // [FIX 6 - CONECTA PERO 0 FRAMES, NUNCA HAY VIDEO] Verificado a mano
            // contra una camara real: con -rtsp_transport tcp, FFmpeg completa el
            // DESCRIBE/SETUP/PLAY de RTSP sin ningun error (se ve el SDP, la
            // resolucion, el framerate...) pero el canal de datos interleaved
            // sobre TCP nunca entrega un solo paquete RTP: 5 segundos conectado,
            // 0 frames, "Output file is empty, nothing was encoded". Es un bug de
            // firmware bastante comun en camaras IP genericas/clones de Hikvision:
            // anuncian soporte de RTP-sobre-TCP interleaved pero no lo implementan
            // bien. Con -rtsp_transport udp, la MISMA camara entrego 30 frames en
            // los mismos 5 segundos sin cambiar nada mas. Como el Gateway corre en
            // la LAN de la casa (misma red que las camaras), UDP no tiene problemas
            // de NAT/firewall que justifiquen forzar TCP - eso solo importaria si el
            // Gateway estuviera fuera de la LAN de la camara, que no es el caso.
            // [FIX 7 - SIN TIMEOUT] Ver punto 6 del historial de bugs arriba de la
            // clase. -timeout y -rw_timeout en 10s: generoso para LAN, evita que
            // ffmpeg quede colgado para siempre si la camara no responde.
            var args = $"-fflags nobuffer -flags low_delay -probesize 65536 -analyzeduration 300000 " +
                      $"-rw_timeout 10000000 -timeout 10000000 " +
                      $"-err_detect ignore_err -rtsp_transport udp -i \"{_rtspUrl}\" " +
                      $"-map 0:v:0 -c:v copy -an -f h264 pipe:1";

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
            _logger?.LogInformation("FFmpeg iniciado correctamente");
            Console.WriteLine("[FFmpegProcess] ✅ FFmpeg iniciado correctamente");

            _ = Task.Run(() => ReadFramesAsync(_cts.Token));
            _ = MonitorProcessAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Error iniciando FFmpeg: {ex.Message}");
            Console.WriteLine($"[FFmpegProcess] ❌ Error: {ex.Message}");
            OnVideoSourceError?.Invoke(ex.Message);
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
    /// Lee el pipe de FFmpeg en un loop, acumulando bytes en _pendingBuffer
    /// (persistente entre lecturas, un solo BlockCopy por lectura) y
    /// disparando la extraccion de NALs/frames cada vez que llegan datos nuevos.
    /// </summary>
    private async Task ReadFramesAsync(CancellationToken cancellationToken)
    {
        if (_outputStream == null) return;

        var readChunk = new byte[65536];

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
            _logger?.LogError($"Error leyendo salida de FFmpeg: {ex.Message}");
            OnVideoSourceError?.Invoke($"Error leyendo salida: {ex.Message}");
        }
    }

    /// <summary>
    /// Agrega bytes al final del buffer pendiente con una sola copia en bloque.
    /// Si no hay espacio libre al final, primero compacta (memmove del rango
    /// [_pendingStart, _pendingEnd) a la posicion 0) y si aun asi no alcanza,
    /// duplica la capacidad del buffer. Debe llamarse con _lock tomado.
    /// </summary>
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
    /// Busca el siguiente start code Annex-B (0x000001 o 0x00000001) a partir
    /// de fromIndex dentro del rango valido [_pendingStart, _pendingEnd).
    /// Devuelve el indice donde empieza y la longitud del start code
    /// encontrado (3 o 4 bytes), o (-1, 0) si no hay ninguno todavia.
    /// </summary>
    private (int index, int prefixLen) FindStartCode(int fromIndex)
    {
        var buf = _pendingBuffer;
        int limit = _pendingEnd - 2;
        for (int i = fromIndex; i < limit; i++)
        {
            if (buf[i] == 0x00 && buf[i + 1] == 0x00)
            {
                if (buf[i + 2] == 0x01)
                {
                    return (i, 3);
                }
                if (i + 3 < _pendingEnd && buf[i + 2] == 0x00 && buf[i + 3] == 0x01)
                {
                    return (i, 4);
                }
            }
        }
        return (-1, 0);
    }

    /// <summary>
    /// Extrae todos los NALs completos que ya esten disponibles en
    /// _pendingBuffer (delimitados por dos start codes consecutivos) y los va
    /// agrupando en access units completos. Consumir un NAL ya extraido es
    /// O(1): solo avanza _pendingStart, sin mover memoria. Debe llamarse con
    /// _lock tomado.
    /// </summary>
    private void ProcessPendingBuffer()
    {
        while (true)
        {
            var (firstIdx, firstPrefixLen) = FindStartCode(_pendingStart);
            if (firstIdx < 0)
            {
                // Todavia no hay ni un start code en el buffer: esperar mas datos.
                return;
            }

            var (secondIdx, _) = FindStartCode(firstIdx + firstPrefixLen);
            if (secondIdx < 0)
            {
                // Hay un NAL empezado pero no confirmado (no llego el siguiente
                // start code todavia). Descartamos basura previa al start code
                // encontrado (O(1): solo mover el puntero) y esperamos mas datos.
                _pendingStart = firstIdx;
                return;
            }

            // NAL completo confirmado: [firstIdx, secondIdx)
            int nalLength = secondIdx - firstIdx;
            var nalBytes = new byte[nalLength];
            Buffer.BlockCopy(_pendingBuffer, firstIdx, nalBytes, 0, nalLength);
            _pendingStart = secondIdx; // O(1): no shift de memoria.

            if (nalBytes.Length <= firstPrefixLen)
            {
                // NAL vacio (solo start code, sin payload) - descartar y seguir.
                continue;
            }

            int nalType = nalBytes[firstPrefixLen] & 0x1F;
            _accessUnitBuffer.AddRange(nalBytes);

            // Tipos 1 (slice no-IDR) y 5 (slice IDR) son VCL: el NAL que
            // efectivamente contiene la imagen y cierra el access unit.
            // SPS(7)/PPS(8)/SEI(6)/AUD(9)/etc se acumulan y se mandan junto
            // con el siguiente slice.
            bool isVclSlice = nalType == 1 || nalType == 5;
            if (isVclSlice)
            {
                EmitAccessUnit();
            }
        }
    }

    /// <summary>
    /// Manda el access unit acumulado (todos los NALs desde el ultimo frame
    /// hasta el slice que lo cierra) como un solo "sample" H264, con una
    /// duracion RTP calculada por el tiempo real transcurrido desde el frame
    /// anterior (en vez del 0 fijo que dejaba el timestamp RTP congelado).
    /// </summary>
    private void EmitAccessUnit()
    {
        if (_accessUnitBuffer.Count == 0)
        {
            return;
        }

        var sample = _accessUnitBuffer.ToArray();
        _accessUnitBuffer.Clear();

        var now = DateTime.UtcNow;
        uint durationRtpUnits;

        if (_lastAccessUnitTime.HasValue)
        {
            var elapsedSeconds = (now - _lastAccessUnitTime.Value).TotalSeconds;
            // Clamp defensivo: si hubo un hueco raro (reconexion, GC pause,
            // etc) no queremos un salto de timestamp absurdo ni tampoco 0.
            elapsedSeconds = Math.Clamp(elapsedSeconds, 1.0 / 60.0, 1.0);
            durationRtpUnits = (uint)Math.Round(elapsedSeconds * RtpClockRate);
        }
        else
        {
            // Primer frame: todavia no hay una medicion real, usamos un
            // fallback razonable (25fps es lo que reportan estas camaras).
            durationRtpUnits = RtpClockRate / DefaultAssumedFps;
        }

        _lastAccessUnitTime = now;

        _frameCount++;
        var elapsed = now - _startTime;

        if (_frameCount == 1)
        {
            Console.WriteLine($"[FFmpegProcess] 🎬 PRIMER FRAME: {elapsed.TotalMilliseconds:F0}ms, tamaño: {sample.Length} bytes");
        }

        if (_frameCount % 25 == 0)
        {
            Console.WriteLine($"[FFmpegProcess] 📹 Frames enviados: {_frameCount}");
        }

        OnVideoSourceEncodedSample?.Invoke(durationRtpUnits, sample);
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            Console.WriteLine($"[FFmpegProcess] {e.Data}");

            if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                OnVideoSourceError?.Invoke(e.Data);
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _isRunning = false;

        _logger?.LogWarning($"FFmpeg terminó con código: {exitCode}");
        Console.WriteLine($"[FFmpegProcess] FFmpeg terminó con código: {exitCode}");

        if (exitCode != 0 && !_cts.IsCancellationRequested)
        {
            OnVideoSourceError?.Invoke($"FFmpeg terminó inesperadamente con código: {exitCode}");
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

    public async Task CloseVideo()
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
                    _logger?.LogWarning("Timeout esperando que FFmpeg termine");
                }
            }

            _isRunning = false;
            _logger?.LogInformation("FFmpeg detenido");
            Console.WriteLine("[FFmpegProcess] FFmpeg detenido");
        }
        catch (Exception ex)
        {
            _logger?.LogError($"Error deteniendo FFmpeg: {ex.Message}");
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
            _accessUnitBuffer.Clear();
        }

        Console.WriteLine("[FFmpegProcess] ✅ Disposed");
    }
}
