namespace CeleCamIp.Server.Embed;

public static class PlayerPage
{
    public const string Html = """
    <!DOCTYPE html>
    <html lang="es">
    <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no" />
    <title>CeleCamIp - Player</title>
    <script src="https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.0/signalr.min.js">
    </script>
    <style>
      * { margin: 0; padding: 0; box-sizing: border-box; }
      html, body { background: #0a0a0a; height: 100%; width: 100%; overflow: hidden; font-family: 'Segoe UI', sans-serif; }
      video { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; background: #000; }
      
      #log-container {
        position: absolute;
        bottom: 20px;
        left: 50%;
        transform: translateX(-50%);
        background: rgba(0,0,0,0.85);
        backdrop-filter: blur(10px);
        padding: 16px 24px;
        border-radius: 12px;
        border: 1px solid rgba(255,255,255,0.08);
        min-width: 280px;
        max-width: 92%;
        text-align: center;
        pointer-events: none;
        font-size: 13px;
        color: #aaa;
        font-family: monospace;
        max-height: 200px;
        overflow-y: auto;
      }
      #log-container .log-connecting { color: #ffd43b; }
      #log-container .log-connected { color: #51cf66; }
      #log-container .log-error { color: #ff6b6b; }
      #log-container .log-info { color: #74c0fc; }
      
      #debug {
        position: absolute;
        top: 10px;
        right: 12px;
        color: rgba(255,255,255,0.15);
        font-size: 9px;
        font-family: monospace;
        text-align: right;
        pointer-events: none;
        line-height: 1.6;
      }
      
      .spinner {
        display: inline-block;
        width: 14px;
        height: 14px;
        border: 2px solid rgba(255,255,255,0.1);
        border-top-color: #ffd43b;
        border-radius: 50%;
        animation: spin 0.8s linear infinite;
        margin-right: 10px;
        vertical-align: middle;
      }
      @keyframes spin { to { transform: rotate(360deg); } }
    </style>
    </head>
    <body>
      <video id="video" autoplay playsinline muted></video>
      
      <div id="log-container">
        <span class="spinner" id="spinner"></span>
        <span id="log-text">Iniciando...</span>
        <div id="log-detail" style="font-size:11px;color:#666;margin-top:4px;"></div>
      </div>
      
      <div id="debug"></div>

      <script>
        // ============================================================
        // ELEMENTOS
        // ============================================================
        var videoEl = document.getElementById('video');
        var logText = document.getElementById('log-text');
        var logDetail = document.getElementById('log-detail');
        var spinner = document.getElementById('spinner');
        var debugEl = document.getElementById('debug');

        // ============================================================
        // LOGGING
        // ============================================================
        function log(message, type, detail) {
          logText.textContent = message;
          logText.className = 'log-' + (type || 'info');
          if (detail) logDetail.textContent = detail;
          else logDetail.textContent = '';
          console.log('[Player]', message, detail || '');
        }

        function setDebug(text) {
          debugEl.textContent = text;
        }

        // ============================================================
        // PAR�METROS
        // ============================================================
        // OJO: URLSearchParams decodifica '+' como espacio (regla de
        // application/x-www-form-urlencoded). Como el token es una API key
        // en base64 y puede contener '+', si alguien pega la URL a mano sin
        // escaparlo bien (%2B), el token llegaria corrupto y el servidor
        // devolveria 401 aunque la key sea correcta. Por eso neutralizamos
        // cualquier '+' literal en la query ANTES de parsear.
        var params = new URLSearchParams(window.location.search.replace(/\+/g, '%2B'));
        var houseId = params.get('houseId');
        var cameraId = params.get('cameraId');
        var token = params.get('token');

        setDebug('H:' + (houseId || '?') + ' | C:' + (cameraId || '?') + ' | T:' + (token ? '?' : '?'));

        console.log('=== CeleCamIp Player ===');
        console.log('houseId:', houseId);
        console.log('cameraId:', cameraId);
        console.log('token:', token ? 'PRESENTE' : 'NO TOKEN');

        // ============================================================
        // VALIDACI�N - TODO DENTRO DE main()
        // ============================================================
        var pc = null;
        var connection = null;
        // Juntamos ac� todos los tracks que lleguen (video Y audio) en un solo
        // MediaStream propio, en vez de depender de event.streams[0]: si el
        // Gateway no agrupa ambos tracks bajo el mismo msid en el SDP, cada
        // track dispara su propio ontrack con su propio stream aislado, y
        // asignar solo el de video al <video> dejaba el audio sin destino.
        var remoteStream = new MediaStream();

        // [FIX] Puente JS -> nativo: la app MAUI (CameraPlayerPage.xaml.cs) escucha
        // navegaciones a "app://status?state=...&message=..." para mostrar/ocultar
        // el loading y los alerts de error. Antes esta funcion no existia y esa
        // navegacion nunca se disparaba, asi que el spinner nativo quedaba
        // colgado para siempre y los errores nunca llegaban a mostrarse como
        // alert nativo (solo quedaban en el log de texto adentro del WebView).
        //
        // [FIX PANTALLA NEGRA TRAS "REPRODUCIENDO"] navegar el frame PRINCIPAL
        // via window.location.href (como se hacia antes) dispara el ciclo de
        // vida de navegacion de Chromium en el frame principal, aunque el
        // lado nativo cancele la navegacion con e.Cancel=true. Confirmado con
        // logcat en un dispositivo real: justo al llamar notifyNativeStatus
        // ('connected') apenas resuelve video.play(), Android libera el
        // MediaCodec de video ("Codec released" / "disconnectFromSurface")
        // y dispara un relayout de la Activity nativa, lo que tira abajo
        // toda la RTCPeerConnection (se ve un DTLS close_notify inmediato
        // del lado del Gateway). La navegacion de un <iframe> oculto en vez
        // del frame principal dispara el mismo evento Navigating del lado
        // nativo (que ya lo intercepta), pero no toca ningun recurso de
        // media del frame principal.
        var statusBridgeFrame = null;
        function notifyNativeStatus(state, message) {
          try {
            var url = 'app://status?state=' + encodeURIComponent(state) +
              (message ? '&message=' + encodeURIComponent(message) : '');
            if (!statusBridgeFrame) {
              statusBridgeFrame = document.createElement('iframe');
              statusBridgeFrame.style.display = 'none';
              document.body.appendChild(statusBridgeFrame);
            }
            statusBridgeFrame.src = url;
          } catch (e) { /* si no corre dentro de un WebView nativo, ignorar */ }
        }

        function startPlayer() {
          notifyNativeStatus('connecting');
          // Validar par�metros
          if (!houseId) {
            log('? Falta: houseId', 'error', 'Agrega &houseId=xxx a la URL');
            notifyNativeStatus('error', 'Falta houseId');
            return;
          }
          if (!cameraId) {
            log('? Falta: cameraId', 'error', 'Agrega &cameraId=xxx a la URL');
            notifyNativeStatus('error', 'Falta cameraId');
            return;
          }
          if (!token) {
            log('? Falta: token', 'error', 'Agrega &token=xxx a la URL');
            notifyNativeStatus('error', 'Falta token');
            return;
          }

          log('Conectando al servidor...', 'connecting');
          main();
        }

        // ============================================================
        // FUNCI�N PRINCIPAL
        // ============================================================
        async function main() {
          try {
            // 1. OBTENER ICE SERVERS
            log('Obteniendo ICE servers...', 'connecting');
            console.log('?? Fetching ICE from: /api/ice-servers?access_token=' + (token ? token.substring(0, 10) + '...' : 'NO TOKEN'));

            var resp = await fetch('/api/ice-servers?access_token=' + encodeURIComponent(token));
            console.log('?? Response status:', resp.status);

            if (!resp.ok) {
              throw new Error('HTTP ' + resp.status + ' - ' + resp.statusText);
            }

            var iceServers = await resp.json();
            console.log('? ICE servers:', iceServers);
            log('ICE servers obtenidos (' + iceServers.length + ')', 'info', 'Usando ' + iceServers.length + ' servidores');

            // 2. CONECTAR AL HUB
            log('Conectando al Hub...', 'connecting');
            console.log('?? Connecting to /hubs/gateway');

            connection = new signalR.HubConnectionBuilder()
              .withUrl('/hubs/gateway', { accessTokenFactory: function() { return token; } })
              .withAutomaticReconnect()
              .build();

            // HANDLERS
            connection.on('ReceiveOffer', async function(camId, offer) {
              console.log('?? Offer recibido para:', camId);
              log('Offer recibido', 'info', 'C�mara: ' + camId);
              if (camId !== cameraId || !pc) return;

              try {
                await pc.setRemoteDescription({ type: offer.type, sdp: offer.sdp });
                var answer = await pc.createAnswer();
                await pc.setLocalDescription(answer);
                await connection.invoke('SendAnswer', houseId, cameraId, { type: answer.type, sdp: answer.sdp });
                log('Answer enviado', 'info', 'Esperando video...');
              } catch (e) {
                log('Error en ReceiveOffer: ' + e.message, 'error');
              }
            });

            connection.on('ReceiveIceCandidate', async function(camId, candidate) {
              if (camId !== cameraId || !pc) return;
              try {
                await pc.addIceCandidate({
                  candidate: candidate.candidate,
                  sdpMid: candidate.sdpMid,
                  sdpMLineIndex: candidate.sdpMLineIndex,
                  usernameFragment: candidate.usernameFragment
                });
              } catch (e) { /* ignorar */ }
            });

            connection.on('StreamError', function(a, b) {
              var reason = b !== undefined ? b : a;
              log('? Error: ' + reason, 'error');
            });

            connection.onreconnecting(function() { log('Reconectando...', 'connecting'); });
            connection.onreconnected(function() { log('Reconectado', 'connected'); });

            await connection.start();
            console.log('? Conectado al Hub');
            log('Conectado al Hub', 'connected');

            // 3. CREAR PEER CONNECTION
            log('Creando PeerConnection...', 'connecting');
            pc = new RTCPeerConnection({ iceServers: iceServers });

            pc.ontrack = function(event) {
              console.log('Track recibido:', event.track.kind);
              // Juntamos TODOS los tracks (video y audio) en remoteStream,
              // sin importar si llegan en el mismo evento.streams[0] o no.
              remoteStream.addTrack(event.track);
              if (videoEl.srcObject !== remoteStream) {
                videoEl.srcObject = remoteStream;
              }

              if (event.track.kind === 'video') {
                // [FIX PANTALLA NEGRA] Asignar srcObject NO garantiza que el
                // video arranque a reproducir, ni con autoplay puesto en el
                // HTML. Confirmado en produccion: pc llegaba a connected,
                // ontrack se disparaba, decia Reproduciendo... y quedaba
                // negro igual, en LAN y en WAN por igual (asi que no es
                // tema de red/ICE/TURN, eso ya andaba). En WebViews
                // embebidos la politica de autoplay programatico suele ser
                // mas estricta: hay que llamar play() a mano y chequear si
                // la Promise se rechaza, para enterarse YA en vez de
                // reportar conectado en falso.
                //
                // El <video> arranca con muted en el HTML para garantizar
                // que el autoplay nunca se bloquee (la politica de autoplay
                // silencioso es mucho mas permisiva que la de autoplay con
                // sonido). Una vez que play() ya arranco en ese estado
                // silencioso, se intenta desmutear: la mayoria de los
                // motores no vuelven a evaluar la politica de autoplay solo
                // por cambiar .muted despues de que la reproduccion ya
                // esta corriendo.
                var playPromise = videoEl.play();
                if (playPromise && typeof playPromise.then === 'function') {
                  playPromise.then(function() {
                    videoEl.muted = false;
                    log('Reproduciendo', 'connected');
                    notifyNativeStatus('connected');
                  }).catch(function(err) {
                    console.error('play() rechazado:', err);
                    log('No se pudo iniciar la reproduccion', 'error', err && err.message);
                    notifyNativeStatus('error', 'El video llego pero no se pudo reproducir: ' + (err && err.message ? err.message : err));
                  });
                } else {
                  videoEl.muted = false;
                  log('Reproduciendo', 'connected');
                  notifyNativeStatus('connected');
                }
              }
            };

            pc.onicecandidate = function(event) {
              if (event.candidate) {
                connection.invoke('SendIceCandidateToGateway', houseId, cameraId, {
                  candidate: event.candidate.candidate,
                  sdpMid: event.candidate.sdpMid,
                  sdpMLineIndex: event.candidate.sdpMLineIndex,
                  usernameFragment: event.candidate.usernameFragment
                }).catch(function() {});
              }
            };

            pc.onconnectionstatechange = function() {
              console.log('Connection state:', pc.connectionState);
              if (pc.connectionState === 'connected') {
                log('? Video conectado', 'connected');
              } else if (pc.connectionState === 'failed') {
                log('? Conexi�n ICE fall�', 'error', 'Verifica STUN/TURN');
                notifyNativeStatus('error', 'No se pudo establecer la conexi�n (ICE fall�).');
              } else if (pc.connectionState === 'disconnected') {
                notifyNativeStatus('error', 'Se perdi� la conexi�n con la c�mara.');
              }
            };

            // 4. SOLICITAR STREAM
            log('Solicitando stream...', 'connecting');
            console.log('?? Invoke RequestStream:', houseId, cameraId);
            await connection.invoke('RequestStream', houseId, cameraId);
            log('Stream solicitado', 'info', 'Esperando oferta...');

          } catch (e) {
            console.error('? ERROR:', e);
            log('? Error: ' + e.message, 'error', e.stack || '');
            notifyNativeStatus('error', e.message);
          }
        }

        // ============================================================
        // INICIAR
        // ============================================================
        startPlayer();

        // ============================================================
        // LIMPIEZA
        // ============================================================
        window.onbeforeunload = function() {
          if (pc) { pc.close(); pc = null; }
          if (connection) { connection.stop(); connection = null; }
        };
      </script>
    </body>
    </html>
    """;
}
