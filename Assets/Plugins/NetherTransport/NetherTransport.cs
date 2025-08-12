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
    private bool isServer;
    private bool isConnected;
    private Dictionary<int, EndPoint> idToConnection;
    private Dictionary<EndPoint, int> connectionToId;
    private LocalConnectionState localConnectionState = LocalConnectionState.Stopped;
    private LocalConnectionState serverConnectionState = LocalConnectionState.Stopped;
    private EndPoint serverAddress;
    private int transportIndex;
    private int clientIndex = 1;
    private int serverConnectionId = -1;
    private Queue<(ArraySegment<byte>, Channel)> toLocalClient = new();
    private Queue<(ArraySegment<byte>, Channel)> toLocalServer = new();

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

    public override void Initialize(NetworkManager networkManager, int transportIndex)
    {
        base.Initialize(networkManager, transportIndex);
        this.transportIndex = transportIndex;
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
        return idToConnection[connectionId].ToString();
    }
    public override void HandleClientConnectionState(ClientConnectionStateArgs connectionStateArgs) =>
        OnClientConnectionState?.Invoke(connectionStateArgs);
    public override void HandleServerConnectionState(ServerConnectionStateArgs connectionStateArgs)
    {
        serverConnectionState = connectionStateArgs.ConnectionState;
        OnServerConnectionState?.Invoke(connectionStateArgs);
    }
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
        return idToConnection.ContainsKey(connectionId) ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;
    }
    public override void SendToServer(byte channelId, ArraySegment<byte> segment)
    {
        if (isServer)
        {
            toLocalServer.Enqueue((segment, (Channel)channelId));
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
            toLocalClient.Enqueue((segment, (Channel)channelId));
            return;
        }
        if (!idToConnection.TryGetValue(connectionId, out var endpoint))
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
        switch (isServer)
        {
            case false when localConnectionState == LocalConnectionState.Stopped:
            case true when serverConnectionState == LocalConnectionState.Stopped:
                return;
        }

        switch (asServer)
        {
            case false when isServer:
            {
                while (toLocalClient.TryDequeue(out var message))
                {
                    HandleClientReceivedDataArgs(new ClientReceivedDataArgs(message.Item1, message.Item2, transportIndex));
                }
                return;
            }
            case true:
            {
                while (toLocalServer.TryDequeue(out var message))
                {
                    HandleServerReceivedDataArgs(new ServerReceivedDataArgs(message.Item1, message.Item2, serverConnectionId,
                        transportIndex));
                }
                break;
            }
        }
        while (socket.Pool(out var sender, out var data, out var length, out var messageType))
        {
            if (isServer)
            {
                if (!connectionToId.TryGetValue(sender, out var connectionId))
                    return;
                HandleServerReceivedDataArgs(new ServerReceivedDataArgs(new ArraySegment<byte>(data, 0, length),
                    messageType == MessageType.Reliable ? Channel.Reliable : Channel.Unreliable,
                    connectionId, transportIndex));
                return;
            }
            HandleClientReceivedDataArgs(new ClientReceivedDataArgs(new ArraySegment<byte>(data, 0, length),
                messageType == MessageType.Reliable ? Channel.Reliable : Channel.Unreliable,
                transportIndex));
        }
    }
    public override void IterateOutgoing(bool asServer)
    {
    }
    public override bool StartConnection(bool asServer)
    {
        idToConnection = new Dictionary<int, EndPoint>();
        connectionToId = new Dictionary<EndPoint, int>();
        toLocalClient.Clear();
        toLocalServer.Clear();
        if (!asServer && isServer)
        {
            // local connection
            localConnectionState = LocalConnectionState.Started;
            HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Started, transportIndex));
            HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, serverConnectionId, transportIndex));
            return true;
        }

        socket = new ManagedSocket(useRelay
            ? new RelaySocket(new UdpSocket(), new IPEndPoint(IPAddress.Parse(relaySettings.Ip), relaySettings.Port)).WithConfiguration(
                relaySettings.ConnectionId, relaySettings.RoomId, relaySettings.RoomSecret, relaySettings.ConnectionSecret)
            : new UdpSocket(asServer ? port : (ushort)0));
        if (asServer)
        {
            isServer = true;
            serverConnectionId = clientIndex++;
            socket.Host();
        }

        socket.OnConnected += (endpoint) =>
        {
            Debug.Log($"Connected: {endpoint} {asServer}");
            if (asServer)
            {
                if (connectionToId.TryGetValue(endpoint, out var connectionId))
                    return;
                connectionId = clientIndex++;
                connectionToId[endpoint] = connectionId;
                idToConnection[connectionId] = endpoint;
                HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, connectionId, transportIndex));
                return;
            }
            if (localConnectionState != LocalConnectionState.Starting)
                return;
            localConnectionState = LocalConnectionState.Started;
            HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Started, transportIndex));
        };

        socket.OnDisconnected += (endpoint) =>
        {
            if (!connectionToId.Remove(endpoint, out var connectionId))
                return;
            idToConnection.Remove(connectionId);
            Debug.Log($"Disconnected: {endpoint} {asServer}");
            if (asServer)
            {
                HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, connectionId, transportIndex));
                if (localConnectionState == LocalConnectionState.Started)
                    HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Stopped, transportIndex));
                return;
            }
            HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Stopped, transportIndex));
        };

        if (asServer)
        {
            if (useRelay)
                StartCoroutine(ConnectToRelay());
            else
                HandleServerConnectionState(new ServerConnectionStateArgs(LocalConnectionState.Started, transportIndex));
        }
        else
        {
            Debug.Log($"Connecting to {ip}:{port}");
            serverAddress = new IPEndPoint(IPAddress.Parse(ip), port);
            StartCoroutine(Connect(serverAddress));
        }
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

    private IEnumerator ConnectToRelay(EndPoint serverEndPoint = null)
    {
        var connectionTask = Task.Run(async () => { await socket.InternalSocket.Setup(serverEndPoint); });
        while (!connectionTask.IsCompleted)
            yield return null;

        if (connectionTask.IsFaulted)
            Debug.LogError(connectionTask.Exception);
        else
        {
            Debug.Log("Relay connection successful");
            relaySettings.ConnectionId = ((RelaySocket)socket.InternalSocket).ConnectionId;
            if(serverEndPoint == null)
                HandleServerConnectionState(new ServerConnectionStateArgs(LocalConnectionState.Started, transportIndex));
        }
    }

    private IEnumerator Connect(EndPoint endpoint)
    {
        if (useRelay)
            yield return ConnectToRelay(endpoint);
        localConnectionState = LocalConnectionState.Starting;
        var connectionTask = Task.Run(async () => await socket.Connect(endpoint));
        while (!connectionTask.IsCompleted)
            yield return null;

        if (connectionTask.IsFaulted)
            Debug.LogError(connectionTask.Exception);
        else if (!connectionTask.Result)
            Debug.LogError("Connection failed");
        else
        {
            Debug.Log("Connection successful");
        }
    }

    public override bool StopConnection(bool server)
    {
        if (localConnectionState == LocalConnectionState.Stopped)
            return true;
        localConnectionState = LocalConnectionState.Stopped;
        socket.Dispose();
        return true;
    }
    public override bool StopConnection(int connectionId, bool immediately)
    {
        idToConnection.Remove(connectionId, out var endpoint);
        return true;
    }
    public override void Shutdown()
    {
        localConnectionState = LocalConnectionState.Stopped;
        socket.Dispose();
    }
    public override int GetMTU(byte channel)
    {
        return 508;
    }
}