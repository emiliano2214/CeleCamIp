namespace CeleCamIp.Shared.WebRtc;

/// <summary>
/// Descripcion SDP (oferta o respuesta) para viajar por SignalR entre el
/// Gateway (offerer, tiene el video) y la app movil (answerer).
/// "Type" es "offer" o "answer", tal cual lo entiende WebRTC en cualquier lado.
/// </summary>
public record SdpDescriptionDto(string Type, string Sdp);

/// <summary>
/// Candidato ICE serializable para trickle ICE entre Gateway y viewer,
/// relayado por el Server. Espeja los campos de RTCIceCandidateInit de SIPSorcery
/// (y los de RTCIceCandidateInit del lado WebRTC nativo/JS, que usa los mismos nombres).
/// </summary>
public record IceCandidateDto(string Candidate, string? SdpMid, ushort? SdpMLineIndex, string? UsernameFragment);

/// <summary>
/// Configuracion de un servidor ICE (STUN o TURN) para armar el RTCConfiguration
/// de una RTCPeerConnection. "Urls" admite un solo valor (ej: "turn:host:puerto"),
/// tal como lo pide RTCIceServer de SIPSorcery.
/// </summary>
public record IceServerDto(string Urls, string? Username = null, string? Credential = null);
