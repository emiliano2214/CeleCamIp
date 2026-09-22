using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CeleCamIp.CameraDiagnostic.Text.DVRIP;

/// <summary>
/// Cliente minimo del protocolo binario DVRIP (tambien conocido como
/// "Sofia"/NetSDK), usado por el firmware Xiongmai/XM y sus OEMs (muchas
/// camaras/DVRs genericos chinos vendidos como "iCSee", "XMEye", etc) en el
/// puerto TCP 34567. No es RTSP ni ONVIF: es el protocolo propietario que
/// usan las apps moviles de esas marcas para configurar el dispositivo.
///
/// Formato de paquete (20 bytes de cabecera + JSON), reconstruido a partir
/// de implementaciones publicas de terceros que reversearon el protocolo
/// (no hay especificacion oficial):
///   byte 0      : head flag, siempre 0xFF
///   byte 1      : version (0x01)
///   bytes 2-3   : reservado (0x00 0x00)
///   bytes 4-7   : SessionID (int32 little-endian)
///   bytes 8-11  : total de paquetes / paquete actual (0 si no esta fragmentado)
///   bytes 12-13 : canal (request) / reservado
///   bytes 14-15 : MsgId (uint16 little-endian) — que comando es
///   bytes 16-19 : longitud del cuerpo JSON que sigue (uint32 little-endian)
///   resto       : cuerpo JSON en UTF-8
///
/// IMPORTANTE: distintos fabricantes/firmwares varian levemente los nombres
/// de campos JSON. Si un comando no responde lo esperado, lo mas rapido es
/// capturar el trafico real de la app oficial (iCSee/XMEye) con Wireshark
/// filtrando tcp.port==34567 y comparar contra lo que arma esta clase.
/// </summary>
public sealed class DvripClient : IDisposable
{
    private const int PuertoPorDefecto = 34567;
    private static readonly TimeSpan TimeoutConexion = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TimeoutRespuesta = TimeSpan.FromSeconds(4);

    // Msgids confirmados contra implementaciones publicas del protocolo DVRIP/Sofia.
    public const int MsgIdLogin = 1000;
    public const int MsgIdConfigGet = 1042;
    public const int MsgIdConfigSet = 1040;
    public const int MsgIdSystemAbilityGet = 1360;
    public const int MsgIdPtz = 1400;

    private TcpClient? _cliente;
    private NetworkStream? _stream;

    public int SessionId { get; private set; }
    public bool Autenticado { get; private set; }

    /// <summary>
    /// Diagnostico: por que el ultimo comando no devolvio JSON valido
    /// (timeout, conexion cerrada, o el texto crudo si no parseo como
    /// JSON). Null si el ultimo comando trajo JSON valido.
    /// </summary>
    public string? UltimoMotivoSinCuerpo { get; private set; }

    public async Task ConectarAsync(string ip, CancellationToken ct, int puerto = PuertoPorDefecto)
    {
        _cliente = new TcpClient();
        var tareaConexion = _cliente.ConnectAsync(ip, puerto, ct).AsTask();
        var completado = await Task.WhenAny(tareaConexion, Task.Delay(TimeoutConexion, ct));

        if (completado != tareaConexion || !_cliente.Connected)
        {
            throw new InvalidOperationException($"No se pudo conectar a {ip}:{puerto} (DVRIP).");
        }

        _stream = _cliente.GetStream();
    }

    /// <summary>
    /// Login DVRIP. La contraseña se manda con el hash propio del
    /// protocolo (ver <see cref="HashXm"/>), no en texto plano ni MD5 hex
    /// estandar — excepto cuando esta vacia: varios firmwares esperan
    /// PassWord="" literal para "sin contraseña" y rechazan el hash de la
    /// cadena vacia. Actualiza <see cref="SessionId"/> si el login es exitoso.
    /// </summary>
    public async Task<(bool Ok, JsonElement? Cuerpo)> LoginAsync(string usuario, string password, CancellationToken ct)
    {
        var pedido = new Dictionary<string, object?>
        {
            ["EncryptType"] = "MD5",
            ["LoginType"] = "DVRIP-Web",
            ["PassWord"] = string.IsNullOrEmpty(password) ? string.Empty : HashXm(password),
            ["UserName"] = usuario ?? string.Empty,
        };

        var (_, cuerpo) = await EnviarAsync(MsgIdLogin, pedido, ct, incluirSessionId: false);

        if (cuerpo is null)
        {
            return (false, null);
        }

        // La respuesta trae "SessionID":"0x0000006b" (string hex) y
        // "Ret": 100 cuando el login es correcto.
        if (cuerpo.Value.TryGetProperty("SessionID", out var sidProp) &&
            sidProp.ValueKind == JsonValueKind.String &&
            sidProp.GetString() is { } sidHex &&
            sidHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(sidHex[2..], System.Globalization.NumberStyles.HexNumber, null, out var sid))
        {
            SessionId = sid;
        }

        var ret = cuerpo.Value.TryGetProperty("Ret", out var retProp) ? retProp.GetInt32() : -1;
        Autenticado = ret == 100;

        return (Autenticado, cuerpo);
    }

    /// <summary>
    /// Pide una seccion de configuracion (CONFIG_GET, msgid 1042). Nombres
    /// de seccion tipicos: "General", "NetWork.NetCommon", "Camera",
    /// "Encode", "Simplify.Encode", "Ptz". La camara devuelve la estructura
    /// completa de esa seccion (valores actuales + rango/opciones cuando el
    /// firmware lo informa).
    /// </summary>
    public async Task<JsonElement?> ObtenerConfigAsync(string nombreSeccion, CancellationToken ct)
    {
        var pedido = new Dictionary<string, object?> { ["Name"] = nombreSeccion };
        var (_, cuerpo) = await EnviarAsync(MsgIdConfigGet, pedido, ct);
        return cuerpo;
    }

    /// <summary>
    /// Envia un comando PTZ (msgid 1400). <paramref name="comando"/> es el
    /// nombre que espera el firmware (ej. "Left", "Right", "Up", "Down",
    /// "ZoomTele", "ZoomWide", "FocusNear", "FocusFar"). "Start" arranca el
    /// movimiento continuo y hay que mandar "Stop" despues para frenarlo.
    /// </summary>
    public async Task<JsonElement?> EnviarComandoPtzAsync(
        string comando, int velocidad, CancellationToken ct, string clase = "Start")
    {
        var pedido = new Dictionary<string, object?>
        {
            ["Name"] = "OPPTZControl",
            ["OPPTZControl"] = new Dictionary<string, object?>
            {
                ["Command"] = comando,
                ["Parameter"] = new Dictionary<string, object?>
                {
                    ["Step"] = new Dictionary<string, object?> { ["Pan"] = velocidad, ["Tilt"] = velocidad, ["Zoom"] = velocidad },
                },
                ["Class"] = clase, // "Start" o "Stop"
                ["SessionID"] = FormatearSessionId(),
            },
        };

        var (_, cuerpo) = await EnviarAsync(MsgIdPtz, pedido, ct);
        return cuerpo;
    }

    private string FormatearSessionId() => $"0x{SessionId:x8}";

    private async Task<(int MsgIdRespuesta, JsonElement? Cuerpo)> EnviarAsync(
        int msgId, Dictionary<string, object?> parametros, CancellationToken ct, bool incluirSessionId = true)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Llamar a ConectarAsync antes de enviar comandos.");
        }

        UltimoMotivoSinCuerpo = null;

        if (incluirSessionId)
        {
            parametros["SessionID"] = FormatearSessionId();
        }

        var json = JsonSerializer.Serialize(parametros);
        var cuerpoBytes = Encoding.UTF8.GetBytes(json);

        var header = new byte[20];
        header[0] = 0xFF; // head flag
        header[1] = 0x01; // version
        // bytes 2-3 reservados en 0

        BitConverter.GetBytes(SessionId).CopyTo(header, 4);
        // bytes 8-11 (total/cur packet) quedan en 0 -> paquete no fragmentado
        BitConverter.GetBytes((ushort)msgId).CopyTo(header, 14);
        BitConverter.GetBytes(cuerpoBytes.Length).CopyTo(header, 16);

        await _stream.WriteAsync(header, ct);
        await _stream.WriteAsync(cuerpoBytes, ct);

        return await LeerRespuestaAsync(ct);
    }

    private async Task<(int MsgIdRespuesta, JsonElement? Cuerpo)> LeerRespuestaAsync(CancellationToken ct)
    {
        var header = await LeerExactoAsync(20, ct);
        if (header is null)
        {
            UltimoMotivoSinCuerpo = "No llego respuesta (timeout o conexion cerrada) al leer la cabecera de 20 bytes.";
            return (-1, null);
        }

        var msgIdRespuesta = BitConverter.ToUInt16(header, 14);
        var longitud = BitConverter.ToInt32(header, 16);

        if (longitud <= 0)
        {
            UltimoMotivoSinCuerpo = $"La camara respondio (msgId {msgIdRespuesta}) pero con cuerpo vacio (longitud {longitud}).";
            return (msgIdRespuesta, null);
        }

        var cuerpoBytes = await LeerExactoAsync(longitud, ct);
        if (cuerpoBytes is null)
        {
            UltimoMotivoSinCuerpo = $"La cabecera anuncio {longitud} bytes de cuerpo pero no llegaron completos (timeout).";
            return (msgIdRespuesta, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(cuerpoBytes);
            return (msgIdRespuesta, doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            var texto = Encoding.UTF8.GetString(cuerpoBytes);
            UltimoMotivoSinCuerpo = $"El cuerpo no parseo como JSON. Texto crudo: {(texto.Length > 300 ? texto[..300] + "..." : texto)}";
            return (msgIdRespuesta, null);
        }
    }

    private async Task<byte[]?> LeerExactoAsync(int cantidad, CancellationToken ct)
    {
        if (_stream is null) return null;

        var buffer = new byte[cantidad];
        var leidos = 0;

        while (leidos < cantidad)
        {
            var tareaLectura = _stream.ReadAsync(buffer.AsMemory(leidos, cantidad - leidos), ct).AsTask();
            var listo = await Task.WhenAny(tareaLectura, Task.Delay(TimeoutRespuesta, ct));

            if (listo != tareaLectura)
            {
                return null; // timeout
            }

            var leidosAhora = tareaLectura.Result;
            if (leidosAhora <= 0)
            {
                return null; // conexion cerrada
            }

            leidos += leidosAhora;
        }

        return buffer;
    }

    /// <summary>
    /// Hash propio del protocolo DVRIP/Sofia para la contraseña (a veces
    /// llamado "XM hash"): MD5 de la contraseña, sumando de a pares de
    /// bytes modulo 62 y mapeando a un alfabeto alfanumerico, quedandose
    /// con los primeros 8 caracteres. No es MD5 hex estandar.
    /// </summary>
    public static string HashXm(string password)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(password));
        var resultado = new StringBuilder(8);

        for (var i = 0; i < 8; i++)
        {
            var n = (hash[2 * i] + hash[2 * i + 1]) % 62;
            char c = n switch
            {
                < 10 => (char)('0' + n),
                < 36 => (char)('A' + (n - 10)),
                _ => (char)('a' + (n - 36)),
            };
            resultado.Append(c);
        }

        return resultado.ToString();
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _cliente?.Dispose();
    }
}
