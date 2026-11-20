using System;
using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Util;
using Steamworks;

namespace HKMultiplayer.Steam;

/// <summary>
/// Steam con el Steamworks.NET que trae el juego. El juego ya inicia Steam (SteamOnlineSubsystem) y llama a
/// SteamAPI.RunCallbacks en cada frame, que es lo que entrega aquí los avisos (salas, invitaciones, peticiones de
/// conexión).
/// <para>
/// La conexión usa SteamNetworking (P2P): mensajes sin garantía de entrega entre cuentas, como UDP, que Steam lleva
/// directo o por sus relés si el router no deja (así nadie tiene que abrir puertos). La fiabilidad y el cifrado con el
/// código de la partida siguen siendo los del mod.
/// </para>
/// </summary>
internal sealed class SteamworksPlatform : ISteamPlatform {
    private const uint HollowKnightAppId = 367520;

    private const int Channel = 0;
    private const int MaxLobbyMembers = 4;

    /// <summary>Búfer fijo reutilizable para enviar sin crear memoria cuando offset != 0.</summary>
    private readonly byte[] _sendBuffer = new byte[NetConstants.MaxDatagramSize];

    private readonly byte[] _discardBuffer = new byte[4096];

    private readonly HashSet<ulong> _expected = new();
    private readonly Callback<P2PSessionRequest_t> _sessionRequest;
    private readonly Callback<GameLobbyJoinRequested_t> _joinRequested;
    private readonly Callback<GameRichPresenceJoinRequested_t> _presenceJoinRequested;
    private readonly CallResult<LobbyCreated_t> _lobbyCreated;
    private readonly CallResult<LobbyEnter_t> _lobbyEntered;
    private Action<ulong> _onCreated;
    private Action<LobbyJoinResult> _onEntered;
    private bool _connectShown;
    private bool _disposed;

    public ulong LocalId { get; }
    public ulong Lobby { get; private set; }
    public event Action<ulong> JoinRequested;

    private SteamworksPlatform(ulong localId) {
        LocalId = localId;
        _sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
        _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(request => {
            Log.Info("Steam: invitación o \"Unirse\" a una partida de un amigo");
            JoinRequested?.Invoke(request.m_steamIDLobby.m_SteamID);
        });
        // "Unirse a la partida" o "Invitar a jugar" desde la lista de amigos de Steam: llega la presencia "connect" del host
        _presenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(request => {
            if (TryParseConnect(request.m_rgchConnect, out var lobby)) {
                Log.Info("Steam: \"Unirse a la partida\" desde la lista de amigos");
                JoinRequested?.Invoke(lobby);
            }
        });
        _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _lobbyEntered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
    }

    /// <summary>La conexión con Steam si el juego lo inició (el de Steam); si no, null.</summary>
    public static ISteamPlatform TryCreate() {
        try {
            if (!SteamAPI.IsSteamRunning()) {
                Log.Info("Steam no está abierto: solo conexión por IP");
                return null;
            }

            var id = SteamUser.GetSteamID();
            if (!id.IsValid()) {
                return null;
            }

            Log.Info("Steam disponible: salas e invitaciones de Steam activas");
            SteamNetworking.AllowP2PPacketRelay(true);
            return new SteamworksPlatform(id.m_SteamID);
        } catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) {
            // El juego no inició Steam (p. ej. abierto sin Steam)
            Log.Info("Steam no se inició en el juego: solo conexión por IP");
            return null;
        }
    }

    // =====================================================================================================
    // Mensajes
    // =====================================================================================================

    public void Send(ulong peer, byte[] buffer, int offset, int length) {
        if (_disposed || length <= 0 || length > NetConstants.MaxDatagramSize) {
            return;
        }

        byte[] data;
        if (offset == 0) {
            data = buffer;
        } else {
            Buffer.BlockCopy(buffer, offset, _sendBuffer, 0, length);
            data = _sendBuffer;
        }

        SteamNetworking.SendP2PPacket(new CSteamID(peer), data, (uint) length, EP2PSend.k_EP2PSendUnreliableNoDelay, Channel);
    }

    public int Receive(byte[] buffer, out ulong from) {
        from = 0;
        while (!_disposed && SteamNetworking.IsP2PPacketAvailable(out var size, Channel)) {
            if (size == 0 || size > buffer.Length) {
                var sink = size <= _discardBuffer.Length ? _discardBuffer : new byte[size];
                SteamNetworking.ReadP2PPacket(sink, (uint) sink.Length, out _, out _, Channel);
                continue;
            }

            if (SteamNetworking.ReadP2PPacket(buffer, (uint) buffer.Length, out var read, out var remote, Channel) && read > 0) {
                from = remote.m_SteamID;
                return (int) read;
            }
        }

        return -1;
    }

    /// <summary>
    /// Alguien quiere hablar con este jugador. Solo se acepta a amigos, a los de la sala y al host al que se une: un
    /// desconocido ni siquiera llega al saludo de la conexión (que además pide el código).
    /// </summary>
    private void OnSessionRequest(P2PSessionRequest_t request) {
        var peer = request.m_steamIDRemote;
        if (_expected.Contains(peer.m_SteamID) || InLobby(peer) ||
            SteamFriends.GetFriendRelationship(peer) == EFriendRelationship.k_EFriendRelationshipFriend) {
            SteamNetworking.AcceptP2PSessionWithUser(peer);
        } else {
            Log.Info("Steam: petición de conexión de alguien que no es amigo ni está en la sala, ignorada");
        }
    }

    private bool InLobby(CSteamID peer) {
        if (Lobby == 0) {
            return false;
        }

        var lobby = new CSteamID(Lobby);
        var count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (var i = 0; i < count; i++) {
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i) == peer) {
                return true;
            }
        }

        return false;
    }

    public void Expect(ulong peer) => _expected.Add(peer);

    /// <summary>La sala de un "+connect_lobby &lt;id&gt;" (la presencia "connect" o la línea de órdenes del juego).</summary>
    public static bool TryParseConnect(string text, out ulong lobby) {
        lobby = 0;
        var parts = (text ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(parts, "+connect_lobby");
        return at >= 0 && at + 1 < parts.Length && ulong.TryParse(parts[at + 1], out lobby) && lobby != 0;
    }

    // =====================================================================================================
    // Salas
    // =====================================================================================================

    public void CreateLobby(bool friendsCanJoin, Action<ulong> done) {
        LeaveLobby();
        _onCreated = done;
        var type = friendsCanJoin ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePrivate;
        _lobbyCreated.Set(SteamMatchmaking.CreateLobby(type, MaxLobbyMembers));
    }

    private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure) {
        var done = _onCreated;
        _onCreated = null;
        if (ioFailure || result.m_eResult != EResult.k_EResultOK) {
            Log.Warn($"Steam: no se pudo crear la sala ({(ioFailure ? "sin conexión con Steam" : result.m_eResult.ToString())})");
            done?.Invoke(0);
            return;
        }

        Lobby = result.m_ulSteamIDLobby;
        // Con la presencia "connect", Steam enseña "Invitar a jugar" (al host) y "Unirse a la partida" (a sus amigos) en la
        // lista de amigos; sin ella solo sale "Invitar a ver". Steam no los separa: con "Solo invitados" también se pone, y
        // a quien pulse "Unirse" sin una invitación a la sala Steam no lo deja entrar (se le dice por qué)
        SteamFriends.SetRichPresence("connect", $"+connect_lobby {Lobby}");
        _connectShown = true;

        done?.Invoke(Lobby);
    }

    public void JoinLobby(ulong lobby, Action<LobbyJoinResult> done) {
        LeaveLobby();
        _onEntered = done;
        _lobbyEntered.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby)));
    }

    private void OnLobbyEntered(LobbyEnter_t result, bool ioFailure) {
        var done = _onEntered;
        _onEntered = null;
        // EChatRoomEnterResponse: 1 bien, 2 no existe, 3 no permitido (privada sin invitación), 4 llena
        var response = ioFailure ? 0u : result.m_EChatRoomEnterResponse;
        var outcome = response switch {
            1 => LobbyJoinResult.Ok,
            2 => LobbyJoinResult.Gone,
            3 => LobbyJoinResult.NotAllowed,
            4 => LobbyJoinResult.Full,
            _ => LobbyJoinResult.Failed
        };
        if (outcome == LobbyJoinResult.Ok) {
            Lobby = result.m_ulSteamIDLobby;
        } else {
            Log.Warn($"Steam: no se pudo entrar en la sala ({outcome}, respuesta {response})");
        }

        done?.Invoke(outcome);
    }

    public void LeaveLobby() {
        if (Lobby == 0) {
            return;
        }

        SteamMatchmaking.LeaveLobby(new CSteamID(Lobby));
        Lobby = 0;
        if (_connectShown) {
            SteamFriends.SetRichPresence("connect", string.Empty);
            _connectShown = false;
        }
    }

    public void SetLobbyData(string key, string value) {
        if (Lobby != 0) {
            SteamMatchmaking.SetLobbyData(new CSteamID(Lobby), key, value);
        }
    }

    public string GetLobbyData(string key) =>
        Lobby != 0 ? SteamMatchmaking.GetLobbyData(new CSteamID(Lobby), key) ?? string.Empty : string.Empty;

    public void OpenInviteDialog() {
        if (Lobby != 0) {
            SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(Lobby));
        }
    }

    public List<SteamFriendGame> FriendGames() {
        var list = new List<SteamFriendGame>();
        var count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (var i = 0; i < count; i++) {
            var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (SteamFriends.GetFriendGamePlayed(friend, out var game) && game.m_gameID.AppID().m_AppId == HollowKnightAppId &&
                game.m_steamIDLobby.IsValid()) {
                list.Add(new SteamFriendGame { Name = SteamFriends.GetFriendPersonaName(friend), Lobby = game.m_steamIDLobby.m_SteamID });
            }
        }

        return list;
    }

    public void Update() {
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;
        LeaveLobby();
        _sessionRequest.Dispose();
        _joinRequested.Dispose();
        _presenceJoinRequested.Dispose();
        _lobbyCreated.Dispose();
        _lobbyEntered.Dispose();
    }
}
