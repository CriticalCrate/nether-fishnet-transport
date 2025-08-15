using FishNet.Managing;
using UnityEngine;

public class Bootstrap : MonoBehaviour
{
    [SerializeField] private NetworkManager networkManager;
    [SerializeField] private bool AsServer = true;
    [SerializeField] private ushort Port = 7777;

    void Start()
    {
        Application.targetFrameRate = 60;
        if(AsServer)
            networkManager.ServerManager.StartConnection(Port);
        networkManager.ClientManager.StartConnection("127.0.0.1", Port);
    }

}