namespace CeleCamIp.Server.Embed;

/// <summary>
/// HTML/JS de un reproductor WebRTC de UNA sola camara, pensado para vivir
/// dentro de un WebView nativo (la app MAUI) en vez de un navegador de
/// escritorio. Es una version reducida de wwwroot/index.html (el viewer de
/// prueba): misma logica de senializacion (JoinAsViewer -> RequestStream ->
/// ReceiveOffer/answer -> trickle ICE), pero:
///
///   - Sin lista de casas/camaras: houseId y cameraId llegan por query string
///     (?houseId=...&cameraId=...&token=...), la app arma esa URL.
///   - Sin prompt() de API key: llega tambien por query string (la misma que
///     usa la app para autenticarse contra el Hub).
///   - Los ICE servers (incluido el TURN real) los pide a /api/ice-servers
///     en vez de tenerlos hardcodeados, para no duplicar el secreto en dos
///     lugares del codigo fuente.
///   - Reporta su estado (connecting/connected/error) al host nativo
///     navegando a una URL con esquema "app://status?...". CameraPlayerPage.xaml.cs
///     intercepta esa navegacion en el evento Navigating del WebView y la
///     cancela antes de que efectivamente navegue, asi nunca se pierde la
///     pagina/JS en ejecucion. Es el truco estandar para mandar eventos de
///     JS a codigo nativo en un WebView de MAUI sin HybridWebView.
///
/// Servida SIEMPRE (cualquier ambiente) en GET /embed/player.html, a
/// diferencia de wwwroot/index.html que solo se sirve en Development.
/// </summary>
public static class PlayerPage
{
    public const string Html = """
    <!DOCTYPE html>
    <html lang="es">
    <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no" />
    <title>CeleCamIp - Player</title>
    <script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js"></script>
    <style>
      html, body { margin:0; padding:0; background:#000; height:100%; width:100%; overflow:hidden; }
      video { position:absolute; inset:0; width:100%; height:100%; object-fit:contain; background:#000; }
    </style>
    </head>
    <body>
      <video id="video" autoplay playsinline muted></video>

    <script>
    // ===================== Parametros de la URL =====================
    const params = new URLSearchParams(window.location.search);
    const houseId = params.get('houseId');
    const cameraId = params.get('cameraId');
    const token = params.get('token');

    const videoEl = document.getElementById('video');

    // ===================== Puente hacia el host nativo =====================
    // Ver el comentario de clase (PlayerPage.cs) sobre por que se usa
    // window.location en vez de un mecanismo mas directo.
    function notifyNative(state, message) {
      try {
        window.location.href = 'app://status?state=' + encodeURIComponent(state) +
          '&message=' + encodeURIComponent(message || '');
      } catch (e) {
        // Si falla (ej: se esta ejecutando suelto en un navegador de escritorio
        // para debug, sin un WebView nativo escuchando), no es un error real.
      }
    }

    if (!houseId || !cameraId || !token) {
      notifyNative('error', 'Faltan parametros (houseId/cameraId/token) en la URL del player.');
    } else {
      main();
    }

    let pc = null;

    async function main() {
      notifyNative('connecting', '');

      let iceServers;
      try {
        const resp = await fetch('/api/ice-servers?access_token=' + encodeURIComponent(token));
        if (!resp.ok) {
          throw new Error('HTTP ' + resp.status);
        }
        const servers = await resp.json();
        iceServers = servers.map(s => ({
          urls: s.urls,
          username: s.username || undefined,
          credential: s.credential || undefined
        }));
      } catch (e) {
        notifyNative('error', 'No se pudo obtener la configuracion ICE del servidor: ' + e.message);
        return;
      }

      const connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/gateway', { accessTokenFactory: () => token })
        .withAutomaticReconnect()
        .build();

      connection.on('ReceiveOffer', async (camId, offer) => {
        if (camId !== cameraId || !pc) {
          return;
        }
        await pc.setRemoteDescription({ type: offer.type, sdp: offer.sdp });
        const answer = await pc.createAnswer();
        await pc.setLocalDescription(answer);
        await connection.invoke('SendAnswer', houseId, cameraId, { type: answer.type, sdp: answer.sdp });
      });

      connection.on('ReceiveIceCandidate', async (camId, candidate) => {
        if (camId !== cameraId || !pc) {
          return;
        }
        try {
          await pc.addIceCandidate({
            candidate: candidate.candidate,
            sdpMid: candidate.sdpMid,
            sdpMLineIndex: candidate.sdpMLineIndex,
            usernameFragment: candidate.usernameFragment
          });
        } catch (e) {
          // Un candidato individual que falla no es fatal; pueden llegar varios.
        }
      });

      connection.on('StreamError', (a, b) => {
        // Puede llegar como (houseId, cameraId, reason) o (cameraId, reason) segun el origen.
        const reason = b !== undefined ? b : a;
        notifyNative('error', 'El servidor reporto un error de stream: ' + reason);
      });

      connection.onreconnecting(() => notifyNative('connecting', 'Reconectando con el servidor...'));

      try {
        await connection.start();
      } catch (e) {
        notifyNative('error', 'No se pudo conectar al servidor: ' + e.message);
        return;
      }

      pc = new RTCPeerConnection({ iceServers });

      pc.ontrack = (event) => {
        videoEl.srcObject = event.streams[0];
        notifyNative('connected', '');
      };

      pc.onicecandidate = (event) => {
        if (event.candidate) {
          connection.invoke('SendIceCandidateToGateway', houseId, cameraId, {
            candidate: event.candidate.candidate,
            sdpMid: event.candidate.sdpMid,
            sdpMLineIndex: event.candidate.sdpMLineIndex,
            usernameFragment: event.candidate.usernameFragment
          }).catch(() => {});
        }
      };

      pc.onconnectionstatechange = () => {
        if (pc.connectionState === 'failed' || pc.connectionState === 'disconnected' || pc.connectionState === 'closed') {
          notifyNative('error', 'Se perdio la conexion de video (' + pc.connectionState + ').');
        }
      };

      try {
        await connection.invoke('RequestStream', houseId, cameraId);
      } catch (e) {
        notifyNative('error', 'No se pudo pedir el stream: ' + e.message);
      }
    }
    </script>
    </body>
    </html>
    """;
}