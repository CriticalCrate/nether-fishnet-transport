using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using ArcaneKit.Nether;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

public class NetherTransport : Transport
{
    [SerializeField] private bool useRelay;
    [SerializeField] private RelaySettings relaySettings;

    private string ip;
    private ushort port = 5000;
    private ManagedSocket socket;
    private Dictionary<int, EndPoint> idToEndpoint;
    private Dictionary<EndPoint, int> connectionToId;
    private LocalConnectionState localConnectionState = LocalConnectionState.Stopped;
    private LocalConnectionState serverConnectionState = LocalConnectionState.Stopped;
    private EndPoint serverAddress;
    private int clientIndex = 1;
    private int serverConnectionId = -1;
    private Queue<(ArraySegment<byte>, Channel)> toLocalClient = new();
    private Queue<(ArraySegment<byte>, Channel)> toLocalServer = new();
    private bool isHost;

    [Serializable]
    private struct RelaySettings
    {
        public string Ip;
        public ushort Port;
        public byte ConnectionId;
        public int RoomId;
        public string RoomSecret;
        public string ConnectionSecret;
    }

    public override void SetPort(ushort port)
    {
        this.port = port;
    }

    public override void SetClientAddress(string address)
    {
        ip = address;
    }

    public override event Action<ClientConnectionStateArgs> OnClientConnectionState;
    public override event Action<ServerConnectionStateArgs> OnServerConnectionState;
    public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;
    public override event Action<ClientReceivedDataArgs> OnClientReceivedData;
    public override event Action<ServerReceivedDataArgs> OnServerReceivedData;

    public override string GetConnectionAddress(int connectionId)
    {
        return idToEndpoint[connectionId].ToString();
    }
    public override void HandleClientConnectionState(ClientConnectionStateArgs connectionStateArgs) =>
        OnClientConnectionState?.Invoke(connectionStateArgs);
    public override void HandleServerConnectionState(ServerConnectionStateArgs connectionStateArgs) =>
        OnServerConnectionState?.Invoke(connectionStateArgs);
    public override void HandleRemoteConnectionState(RemoteConnectionStateArgs connectionStateArgs) =>
        OnRemoteConnectionState?.Invoke(connectionStateArgs);
    public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs receivedDataArgs) =>
        OnClientReceivedData?.Invoke(receivedDataArgs);
    public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs receivedDataArgs) =>
        OnServerReceivedData?.Invoke(receivedDataArgs);
    public override LocalConnectionState GetConnectionState(bool server)
    {
        return server
            ? LocalConnectionState.Started
            : localConnectionState;
    }
    public override RemoteConnectionState GetConnectionState(int connectionId)
    {
        return idToEndpoint.ContainsKey(connectionId) ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;
    }
    public override void SendToServer(byte channelId, ArraySegment<byte> segment)
    {
        if (isHost)
        {
            //copy
            toLocalServer.Enqueue((segment.ToArray(), (Channel)channelId));
            return;
        }

        switch ((Channel)channelId)
        {
            case Channel.Reliable: socket.SendReliable(serverAddress, segment.ToArray(), segment.Count); break;
            case Channel.Unreliable: socket.SendUnreliable(serverAddress, segment.ToArray(), segment.Count); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelId), channelId, null);
        }
    }
    public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
    {
        if (connectionId == serverConnectionId)
        {
            toLocalClient.Enqueue((segment.ToArray(), (Channel)channelId));
            return;
        }
        if (!idToEndpoint.TryGetValue(connectionId, out var endpoint))
            return;
        switch ((Channel)channelId)
        {
            case Channel.Reliable: socket.SendReliable(endpoint, segment.ToArray(), segment.Count); break;
            case Channel.Unreliable: socket.SendUnreliable(endpoint, segment.ToArray(), segment.Count); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(channelId), channelId, null);
        }
    }
    public override void IterateIncoming(bool asServer)
    {
        switch (isHost)
        {
            case false when localConnectionState != LocalConnectionState.Started:
            case true when serverConnectionState != LocalConnectionState.Started:
                return;
        }
        switch (asServer)
        {
            case false when isHost:
            {
                while (toLocalClient.TryDequeue(out var message))
                {
                    HandleClientReceivedDataArgs(new ClientReceivedDataArgs(message.Item1, message.Item2, Index));
                }
                return;
            }
            case true:
            {
                while (toLocalServer.TryDequeue(out var message))
                {
                    HandleServerReceivedDataArgs(new ServerReceivedDataArgs(message.Item1, message.Item2, serverConnectionId,
                        Index));
                }
                break;
            }
        }
        while (socket.Pool(out var sender, out var data, out var length, out var messageType))
        {
            if (isHost)
            {
                if (!connectionToId.TryGetValue(sender, out var connectionId))
                    return;
                HandleServerReceivedDataArgs(new ServerReceivedDataArgs(new ArraySegment<byte>(data, 0, length),
                    messageType == MessageType.Reliable ? Channel.Reliable : Channel.Unreliable,
                    connectionId, Index));
                return;
            }
            HandleClientReceivedDataArgs(new ClientReceivedDataArgs(new ArraySegment<byte>(data, 0, length),
                messageType == MessageType.Reliable ? Channel.Reliable : Channel.Unreliable,
                Index));
        }
    }
    public override void IterateOutgoing(bool asServer)
    {
    }
    public override bool StartConnection(bool asServer)
    {
        idToEndpoint = new Dictionary<int, EndPoint>();
        connectionToId = new Dictionary<EndPoint, int>();
        toLocalClient.Clear();
        toLocalServer.Clear();
        switch (asServer)
        {
            case true:
                isHost = true;
                serverConnectionId = clientIndex++;
                idToEndpoint.Add(serverConnectionId, null);
                break;
            case false when isHost:
                // local connection
                localConnectionState = LocalConnectionState.Started;
                HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Started, Index));
                HandleRemoteConnectionState(
                    new RemoteConnectionStateArgs(RemoteConnectionState.Started, serverConnectionId, Index));
                return true;
        }

        socket = new ManagedSocket(useRelay
            ? new RelaySocket(new UdpSocket(), new IPEndPoint(IPAddress.Parse(relaySettings.Ip), relaySettings.Port)).WithConfiguration(
                relaySettings.ConnectionId, relaySettings.RoomId, relaySettings.RoomSecret, relaySettings.ConnectionSecret)
            : new UdpSocket(asServer ? port : (ushort)0));
        socket.OnConnected += endpoint =>
        {
            if (!asServer)
                return;
            if (connectionToId.TryGetValue(endpoint, out var connectionId))
                return;
            connectionId = clientIndex++;
            connectionToId[endpoint] = connectionId;
            idToEndpoint[connectionId] = endpoint;
            HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, connectionId, Index));
        };

        socket.OnDisconnected += endpoint =>
        {
            if (!asServer)
            {
                localConnectionState = LocalConnectionState.Stopped;
                HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Stopped, Index));
                return;
            }

            if (!connectionToId.Remove(endpoint, out var connectionId))
                return;
            idToEndpoint.Remove(connectionId);
            HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, connectionId, Index));
            if (localConnectionState == LocalConnectionState.Started)
                HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Stopped, Index));
        };

        if (asServer)
        {
            if (useRelay)
                StartCoroutine(ConnectToRelay());
            else
            {
                serverConnectionState = LocalConnectionState.Started;
                HandleServerConnectionState(new ServerConnectionStateArgs(serverConnectionState, Index));
            }
            return true;
        }

        serverAddress = new IPEndPoint(IPAddress.Parse(ip), port);
        StartCoroutine(Connect(serverAddress));
        return true;
    }

    public void ConfigureRelay(string ip, ushort port, byte connectionId, int roomId, string roomSecret, string connectionSecret)
    {
        relaySettings = new RelaySettings
        {
            Ip = ip,
            Port = port,
            ConnectionId = connectionId,
            RoomId = roomId,
            RoomSecret = roomSecret,
            ConnectionSecret = connectionSecret
        };
    }

    private IEnumerator ConnectToRelay(EndPoint serverEndPoint = null, Action<bool> callback = null)
    {
        Debug.Log("Connecting to relay server...");
        localConnectionState = LocalConnectionState.Starting;
        serverConnectionState = LocalConnectionState.Starting;
        yield return ((RelaySocket)socket.InternalSocket).ConnectToRelay(connected =>
        {
            if (!connected)
            {
                Debug.LogError("Failed to connect to relay server");
                localConnectionState = LocalConnectionState.Stopped;
                serverConnectionState = LocalConnectionState.Stopped;
                callback?.Invoke(false);
                return;
            }
            Debug.Log("Connected to relay server");
            relaySettings.ConnectionId = ((RelaySocket)socket.InternalSocket).ConnectionId;
            ((RelaySocket)socket.InternalSocket).AssignHost(serverEndPoint);
            if (isHost)
            {
                localConnectionState = LocalConnectionState.Started;
                serverConnectionState = LocalConnectionState.Started;
                HandleServerConnectionState(new ServerConnectionStateArgs(serverConnectionState, Index));
            }
            callback?.Invoke(true);
        });
    }

    private IEnumerator Connect(EndPoint endpoint)
    {
        if (useRelay)
        {
            var success = false;
            yield return ConnectToRelay(endpoint, connected => success = connected);
            if (!success)
                yield break;
        }

        Debug.Log("Connecting to server...");
        yield return socket.Connect(endpoint, connected =>
        {
            if (connected)
            {
                Debug.Log("Connected to server");
                localConnectionState = LocalConnectionState.Started;
                serverConnectionState = LocalConnectionState.Started;
                HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Started, Index));
                return;
            }

            Debug.LogError("Failed to connect to server");
            localConnectionState = LocalConnectionState.Stopped;
            serverConnectionState = LocalConnectionState.Stopped;
        });
    }

    public override bool StopConnection(bool server)
    {
        if (server && serverConnectionState == LocalConnectionState.Started)
        {
            foreach (var idToEndpoint in idToEndpoint)
                socket.Disconnect(idToEndpoint.Value);
            socket.Dispose();
        }
        else if (localConnectionState == LocalConnectionState.Started)
        {
            socket.Disconnect(serverAddress);
            socket.Dispose();
        }
        localConnectionState = LocalConnectionState.Stopped;
        serverConnectionState = LocalConnectionState.Stopped;
        return true;
    }
    public override bool StopConnection(int connectionId, bool immediately)
    {
        idToEndpoint.Remove(connectionId, out var endpoint);
        socket.Disconnect(endpoint);
        return true;
    }
    public override void Shutdown()
    {
        localConnectionState = LocalConnectionState.Stopped;
        serverConnectionState = LocalConnectionState.Stopped;
        socket.Dispose();
    }
    public override int GetMTU(byte channel)
    {
        return 508;
    }
}