using System;
using System.Collections.Generic;
using System.Net;
using ArcaneKit.Nether;
using FishNet.Managing;
using FishNet.Transporting;

public class NetherTransport : Transport
{
    public string Ip;
    public ushort Port = 5000;

    private ManagedSocket socket;
    private bool isServer;
    private bool isConnected;
    private Dictionary<int, EndPoint> idToConnection;
    private Dictionary<EndPoint, int> connectionToId;
    private LocalConnectionState localConnectionState = LocalConnectionState.Stopped;
    private EndPoint serverAddress;
    private int transportIndex;
    private int clientIndex = 1;
    private int serverConnectionId = -1;
    private Queue<(ArraySegment<byte>, Channel)> toLocalClient = new();
    private Queue<(ArraySegment<byte>, Channel)> toLocalServer = new();

    public override void Initialize(NetworkManager networkManager, int transportIndex)
    {
        base.Initialize(networkManager, transportIndex);
        this.transportIndex = transportIndex;
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
        if (!asServer && isServer)
        {
            while (toLocalClient.TryDequeue(out var message))
            {
                HandleClientReceivedDataArgs(new ClientReceivedDataArgs(message.Item1, message.Item2, transportIndex));
            }
            return;
        }

        if (asServer)
        {
            while (toLocalServer.TryDequeue(out var message))
            {
                HandleServerReceivedDataArgs(new ServerReceivedDataArgs(message.Item1, message.Item2, serverConnectionId, transportIndex));
            }
        }

        while (socket.Pool(out var sender, out var data, out var length))
        {
            if (isServer)
            {
                if (!connectionToId.TryGetValue(sender, out var connectionId))
                    return;
                HandleServerReceivedDataArgs(new ServerReceivedDataArgs(new ArraySegment<byte>(data, 0, length), Channel.Reliable,
                    connectionId, transportIndex));
                return;
            }

            if (sender != serverAddress)
                return;
            HandleClientReceivedDataArgs(new ClientReceivedDataArgs(new ArraySegment<byte>(data, 0, length), Channel.Reliable,
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
        if (!asServer && isServer)
        {
            // local connection
            localConnectionState = LocalConnectionState.Started;
            HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Started, transportIndex));
            HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, serverConnectionId, transportIndex));
            return true;
        }

        isServer = asServer;
        localConnectionState = LocalConnectionState.Starting;
        socket = new ManagedSocket(new UdpSocket(isServer ? Port : (ushort)0));
        if (asServer)
        {
            serverConnectionId = clientIndex++;
        }

        socket.OnConnected += (endpoint) =>
        {
            if (isServer)
            {
                if (connectionToId.TryGetValue(endpoint, out var connectionId))
                    return;
                connectionId = clientIndex++;
                connectionToId[endpoint] = connectionId;
                idToConnection[connectionId] = endpoint;
                HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Started, clientIndex, transportIndex));
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
            if (isServer)
            {
                HandleRemoteConnectionState(new RemoteConnectionStateArgs(RemoteConnectionState.Stopped, connectionId, transportIndex));
                if (localConnectionState == LocalConnectionState.Started)
                    HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Stopped, transportIndex));
                return;
            }
            HandleClientConnectionState(new ClientConnectionStateArgs(LocalConnectionState.Stopped, transportIndex));
        };

        if (asServer)
            HandleServerConnectionState(new ServerConnectionStateArgs(LocalConnectionState.Started, transportIndex));
        if (!isServer)
            socket.SendReliable(new IPEndPoint(IPAddress.Parse(Ip), Port), new byte[] { 4 }, 4);
        return true;
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