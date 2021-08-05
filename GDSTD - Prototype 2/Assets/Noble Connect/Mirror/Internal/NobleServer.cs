using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NobleConnect.Ice;
using UnityEngine;
using Mirror;
using System.Collections.Concurrent;
#if LITENETLIB_TRANSPORT
using LiteNetLib;
#endif
using System.Reflection;

namespace NobleConnect.Mirror
{
    /// <summary>Adds relay, punchthrough, and port-forwarding support to the Mirror NetworkServer</summary>
    /// <remarks>
    /// Use the Listen method to start listening for incoming connections.
    /// </remarks>
    public class NobleServer
    {

        #region Public Properties

        static IPEndPoint LocalHostEndPoint = null;
        /// <summary>This is the address that clients should connect to. It is assigned by the relay server.</summary>
        /// <remarks>
        /// Note that this is not the host's actual IP address, but one assigned to the host by the relay server.
        /// When clients connect to this address, Noble Connect will find the best possible connection and use it.
        /// This means that the client may actually end up connecting to an address on the local network, or an address
        /// on the router, or an address on the relay. But you don't need to worry about any of that, it is all
        /// handled for you internally.
        /// </remarks>
        public static IPEndPoint HostEndPoint {
            get {
                if (baseServer != null) 
                {
                    return baseServer.RelayEndPoint;
                }
                else
                {
                    return LocalHostEndPoint;
                }
            }
            set {
                LocalHostEndPoint = value;
            }
        }

        /// <summary>Initial timeout before resending refresh messages. This is doubled for each failed resend.</summary>
        public static float allocationResendTimeout = .1f;

        /// <summary>Request timeout.</summary>
        /// <remarks>
        /// This effects how long to wait before considering a request to have failed.
        /// Requests are used during the punchthrough process and for setting up and maintaining relays.
        /// If you are allowing cross-region play or expect high latency you can increase this so that requests won't time out.
        /// The drawback is that waiting longer for timeouts causes it take take longer to detect actual failed requests so the
        /// connection process may take longer.
        /// </remarks>
        public static float requestTimeout = .2f;

        /// <summary>Max number of times to try and resend refresh messages before giving up and shutting down the relay connection.</summary>
        /// <remarks>
        /// If refresh messages fail for 30 seconds the relay connection will be closed remotely regardless of these settings.
        /// </remarks>
        public static int maxAllocationResends = 8;

        /// <summary>How long a relay will stay alive without being refreshed (in seconds)</summary>
        /// <remarks>
        /// Setting this value higher means relays will stay alive longer even if the host temporarily loses connection or otherwise fails to send the refresh request in time.
        /// This can be helpful to maintain connection on an undependable network or when heavy application load (such as loading large levels synchronously) temporarily prevents requests from being processed.
        /// The drawback is that CCU is used for as long as the relay stays alive, so players that crash or otherwise don't clean up properly can cause lingering CCU usage for up to relayLifetime seconds.
        /// </remarks>
        public static int relayLifetime = 60;
        
        /// <summary>How often to refresh the relay to keep it alive, in seconds</summary>
        public static int relayRefreshTime = 30;

        #endregion Public Properties

        #region Internal Properties

        const string TRANSPORT_WARNING_MESSAGE = "You must use a transport that supports UDP in order to use Mirror with NobleConnect.\n" +
                                                "I recommend the default KcpTransport.";

        static Peer baseServer;

        /// <summary>A method to call if something goes wrong like reaching ccu or bandwidth limit</summary>
        static Action<string> onFatalError = null;
        /// <summary>Keeps track of which end point each NetworkConnection belongs to so that when they disconnect we know which Bridge to destroy</summary>
        static Dictionary<NetworkConnection, IPEndPoint> endPointByConnection = new Dictionary<NetworkConnection, IPEndPoint>();

        static IceConfig nobleConfig = new IceConfig();

        #endregion Internal Properties

        #region Public Interface

        /// <summary>Initialize the server using NobleConnectSettings. The region used is determined by the Relay Server Address in the NobleConnectSettings.</summary>
        /// <remarks>\copydetails NobleClient::NobleClient(HostTopology,Action)</remarks>
        /// <param name="topo">The HostTopology to use for the NetworkClient. Must be the same on host and client.</param>
        /// <param name="onFatalError">A method to call if something goes horribly wrong.</param>
        public static void InitializeHosting(int listenPort, GeographicRegion region = GeographicRegion.AUTO, Action<string, ushort> onPrepared = null, Action<string> onFatalError = null, bool forceRelayOnly = false)
        {
            _Init(listenPort, RegionURL.FromRegion(region), onPrepared, onFatalError, forceRelayOnly);
        }

        /// <summary>
        /// Initialize the client using NobleConnectSettings but connect to specific relay server address.
        /// This method is useful for selecting the region to connect to at run time when starting the client.
        /// </summary>
        /// <remarks>\copydetails NobleClient::NobleClient(HostTopology,Action)</remarks>
        /// <param name="relayServerAddress">The url or ip of the relay server to connect to</param>
        /// <param name="topo">The HostTopology to use for the NetworkClient. Must be the same on host and client.</param>
        /// <param name="onPrepared">A method to call when the host has received their HostEndPoint from the relay server.</param>
        /// <param name="onFatalError">A method to call if something goes horribly wrong.</param>
        public static void InitializeHosting(int listenPort, string relayServerAddress, Action<string, ushort> onPrepared = null, Action<string> onFatalError = null, bool forceRelayOnly = false)
        {
            _Init(listenPort, relayServerAddress, onPrepared, onFatalError, forceRelayOnly);
        }


        /// <summary>Initialize the NetworkServer and Ice.Controller</summary>
        static void _Init(int listenPort, string relayServerAddress, Action<string, ushort> onPrepared = null, Action<string> onFatalError = null, bool forceRelayOnly = false)
        {
            var settings = (NobleConnectSettings)Resources.Load("NobleConnectSettings", typeof(NobleConnectSettings));
            var platform = Application.platform;

            NobleServer.onFatalError = onFatalError;

            nobleConfig = new IceConfig
            {
                iceServerAddress = relayServerAddress,
                icePort = settings.relayServerPort,
                useSimpleAddressGathering = (platform == RuntimePlatform.IPhonePlayer || platform == RuntimePlatform.Android) && !Application.isEditor,
                onFatalError = OnFatalError,
                forceRelayOnly = forceRelayOnly,
                allocationLifetime = relayLifetime,
                refreshTime = relayRefreshTime,
                maxAllocationRetransmissionCount = maxAllocationResends,
                allocationRetransmissionTimeout = (int)(allocationResendTimeout*1000),
                defaultRetransmissionTimeout = (int)(requestTimeout*1000)
            };

            if (!string.IsNullOrEmpty(settings.gameID))
            {
                if (settings.gameID.Length % 4 != 0) throw new System.ArgumentException("Game ID is wrong. Re-copy it from the Dashboard on the website.");
                string decodedGameID = Encoding.UTF8.GetString(Convert.FromBase64String(settings.gameID));
                string[] parts = decodedGameID.Split('\n');

                if (parts.Length == 3)
                {
                    nobleConfig.origin = parts[0];
                    nobleConfig.username = parts[1];
                    nobleConfig.password = parts[2];
                }
            }

            baseServer = new Peer(nobleConfig);

            NetworkServer.OnConnectedEvent = OnServerConnect;
            NetworkServer.OnDisconnectedEvent = OnServerDisconnect;

            if (baseServer == null)
            {
                Logger.Log("NobleServer.Init() must be called before InitializeHosting() to specify region and error handler.", Logger.Level.Fatal);
            }
            baseServer.InitializeHosting(listenPort, onPrepared);
        }

        /// <summary>If you are using the NetworkServer directly you must call this method every frame.</summary>
        /// <remarks>
        /// The NobleNetworkManager and NobleNetworkLobbyManager handle this for you but you if you are
        /// using the NobleServer directly you must make sure to call this method every frame.
        /// </remarks>
        static public void Update()
        {
            if (baseServer != null) baseServer.Update();
        }

        /// <summary>Start listening for incoming connections</summary>
        /// <param name="maxPlayers">The maximum number of players</param>
        /// <param name="port">The port to listen on. Defaults to 0 which will use a random port</param>
        /// <param name="onPrepared">A method to call when the host has received their HostEndPoint from the relay server.</param>
        static public void Listen(int port = 0, GeographicRegion region = GeographicRegion.AUTO, Action<string, ushort> onPrepared = null, Action<string> onFatalError = null, bool forceRelayOnly = false)
        {
            // Store or generate the server port
            if (port == 0)
            {
                // Use a randomly generated endpoint
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    port = (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
                }
            }

            InitializeHosting(port, region, onPrepared, onFatalError, forceRelayOnly);

            SetListenPort((ushort)port);
            // Server Go!
            NetworkServer.Listen(10);
        }

        static public void SetListenPort(ushort port)
        {
            bool hasUDP = false;
            var transportType = Transport.activeTransport.GetType();
#if LITENETLIB_TRANSPORT
            if (transportType == typeof(LiteNetLibTransport))
            {
                hasUDP = true;
                var liteNet = (LiteNetLibTransport)Transport.activeTransport;
                liteNet.port = (ushort)port;
            }
#endif
#if IGNORANCE
            if (transportType.IsSubclassOf(typeof(IgnoranceTransport.Ignorance)) ||
                transportType == typeof(IgnoranceTransport.Ignorance))
            {
                hasUDP = true;
                var ignorance = (IgnoranceTransport.Ignorance)Transport.activeTransport;
                ignorance.port = port;
            }
#endif
            if (!hasUDP)
            {
                throw new Exception(TRANSPORT_WARNING_MESSAGE);
            }
        }

        public static ushort GetTransportPort()
        {
            bool hasUDP = false;
            var transportType = Transport.activeTransport.GetType();
#if LITENETLIB_TRANSPORT
            if (transportType == typeof(LiteNetLibTransport))
            {
                hasUDP = true;
                return (ushort)((LiteNetLibTransport)Transport.activeTransport).port;
            }
#endif
#if IGNORANCE
            if (transportType.IsSubclassOf(typeof(IgnoranceTransport.Ignorance)) ||
                transportType == typeof(IgnoranceTransport.Ignorance))
            {
                hasUDP = true;
                return (ushort)((IgnoranceTransport.Ignorance)Transport.activeTransport).port;
            }
#endif
            if (transportType.IsSubclassOf(typeof(kcp2k.KcpTransport)) ||
                transportType == typeof(kcp2k.KcpTransport))
            {
                hasUDP = true;
                return (ushort)((kcp2k.KcpTransport)Transport.activeTransport).Port;
            }
            if (!hasUDP)
            {
                throw new Exception(TRANSPORT_WARNING_MESSAGE);
            }

            return 0;
        }

        public static void SetTransportPort(ushort port)
        {
            bool hasUDP = false;
            var transportType = Transport.activeTransport.GetType();
#if LITENETLIB_TRANSPORT
            if (transportType == typeof(LiteNetLibTransport))
            {
                hasUDP = true;
                var liteNet = (LiteNetLibTransport)Transport.activeTransport;
                liteNet.port = (ushort)port;
            }
#endif
#if IGNORANCE
            if (transportType.IsSubclassOf(typeof(IgnoranceTransport.Ignorance)) || 
                transportType == typeof(IgnoranceTransport.Ignorance))
            {
                hasUDP = true;
                var ignorance = (IgnoranceTransport.Ignorance)Transport.activeTransport;
                ignorance.port = port;
            }
#endif
            if (transportType.IsSubclassOf(typeof(kcp2k.KcpTransport)) ||
                transportType == typeof(kcp2k.KcpTransport))
            {
                hasUDP = true;
                var kcp = (kcp2k.KcpTransport)Transport.activeTransport;
                kcp.Port = port;
            }
            if (!hasUDP)
            {
                throw new Exception(TRANSPORT_WARNING_MESSAGE);
            }
        }

        static public void UnregisterHandler<T>() where T : struct, NetworkMessage
        {
            NetworkServer.UnregisterHandler<T>();
        }

        static public void ClearHandlers()
        {
            NetworkServer.ClearHandlers();
        }

        /// <summary>Clean up and free resources. Called automatically when garbage collected.</summary>
        /// <remarks>
        /// You shouldn't need to call this directly. It will be called automatically when an unused
        /// NobleServer is garbage collected or when shutting down the application.
        /// </remarks>
        /// <param name="disposing"></param>
        public static void Dispose()
        {
            if (baseServer != null)
            {
                baseServer.Dispose();
                baseServer = null;
            }
        }

#endregion Public Interface

        #region Handlers

        static public void OnServerConnect(NetworkConnection conn)
        {
            if (baseServer == null) return;
            
            if (conn.GetType() == typeof(NetworkConnectionToClient))
            {
                var transportType = Transport.activeTransport.GetType();
#if LITENETLIB_TRANSPORT
                if (transportType == typeof(LiteNetLibTransport))
                {
                    var liteNet = (LiteNetLibTransport)Transport.activeTransport;
                    endPointByConnection[conn] = liteNet.ServerGetClientIPEndPoint(conn.connectionId);
                }
#endif
#if IGNORANCE
                if (transportType.IsSubclassOf(typeof(IgnoranceTransport.Ignorance)) || 
                    transportType == typeof(IgnoranceTransport.Ignorance))
                {
                    var ignorance = ((IgnoranceTransport.Ignorance)Transport.activeTransport);
                    var connectionLookupDictField = typeof(IgnoranceTransport.Ignorance).GetField("ConnectionLookupDict", BindingFlags.NonPublic | BindingFlags.Instance);
                    var connectionLookupDict = (Dictionary<int, IgnoranceTransport.PeerConnectionData>)connectionLookupDictField.GetValue(ignorance);
                    IgnoranceTransport.PeerConnectionData result;
                    if (connectionLookupDict.TryGetValue(conn.connectionId, out result))
                    {
                        endPointByConnection[conn] = new IPEndPoint(IPAddress.Parse(result.IP), result.Port);
                    }
                }
#endif
                if (transportType.IsSubclassOf(typeof(kcp2k.KcpTransport)) ||
                    transportType == typeof(kcp2k.KcpTransport))
                {
                    var kcp = ((kcp2k.KcpTransport)Transport.activeTransport);
                    var kcpServerField = typeof(kcp2k.KcpTransport).GetField("server", BindingFlags.NonPublic | BindingFlags.Instance);
                    var kcpServer = (kcp2k.KcpServer)kcpServerField.GetValue(kcp);
                    if (kcpServer.connections.TryGetValue(conn.connectionId, out kcp2k.KcpServerConnection result))
                    {
                        endPointByConnection[conn] = (IPEndPoint)result.GetRemoteEndPoint();
                    }
                }
                baseServer.FinalizeConnection(endPointByConnection[conn]);
            }
        }

        /// <summary>Called on the server when a client disconnects</summary>
        /// <remarks>
        /// Some memory and ports are freed here.
        /// </remarks>
        /// <param name="message"></param>
        static public void OnServerDisconnect(NetworkConnection conn)
        {
            if (endPointByConnection.ContainsKey(conn))
            {
                IPEndPoint endPoint = endPointByConnection[conn];
                if (baseServer != null) baseServer.EndSession(endPoint);
                endPointByConnection.Remove(conn);
            }
            //conn.Dispose();
        }

        /// <summary>Called when a fatal error occurs.</summary>
        /// <remarks>
        /// This usually means that the ccu or bandwidth limit has been exceeded. It will also
        /// happen if connection is lost to the relay server for some reason.
        /// </remarks>
        /// <param name="errorString">A string with more info about the error</param>
        static private void OnFatalError(string errorString)
        {
            Logger.Log("Shutting down because of fatal error: " + errorString, Logger.Level.Fatal);
            NetworkServer.Shutdown();
            if (onFatalError != null) onFatalError(errorString);
        }

        #endregion Handlers

        #region Static NetworkServer Wrapper
        static public NetworkConnection localConnection { get { return NetworkServer.localConnection; } }
        static public Dictionary<int, NetworkConnectionToClient> connections { get { return NetworkServer.connections; } }
        public static bool dontListen { get { return NetworkServer.dontListen; } set { NetworkServer.dontListen = value; } }
        public static bool active { get { return NetworkServer.active; } }
        public static bool localClientActive { get { return NetworkServer.localClientActive; } }
        public static void Shutdown() { NetworkServer.Shutdown(); }
        public static void SendToAll<T>(T msg, int channelId = Channels.DefaultReliable, bool sendToReadyOnly = false) where T : struct, NetworkMessage
        {
            NetworkServer.SendToAll<T>(msg, channelId, sendToReadyOnly);
        }
        public static void SendToReady<T>(NetworkIdentity identity, T msg, bool includeSelf = true, int channelId = Channels.DefaultReliable) where T : struct, NetworkMessage
        {
            NetworkServer.SendToReady<T>(identity, msg, includeSelf, channelId);
        }
        public static void SendToReady<T>(NetworkIdentity identity, T msg, int channelId = Channels.DefaultReliable) where T : struct, NetworkMessage
        {
            SendToReady(identity, msg, true, channelId);
        }
        static public void DisconnectAll() { NetworkServer.DisconnectAll(); }
        static public void SendToClientOfPlayer<T>(NetworkIdentity player, T msg) where T : struct, NetworkMessage
        { 
            NetworkServer.SendToClientOfPlayer<T>(player, msg);
        }
        static public bool ReplacePlayerForConnection(NetworkConnection conn, GameObject player, Guid assetId, bool keepAuthority = false)
        {
            return NetworkServer.ReplacePlayerForConnection(conn, player, assetId, keepAuthority);
        }
        public static bool ReplacePlayerForConnection(NetworkConnection conn, GameObject player, bool keepAuthority = false)
        {
            return NetworkServer.ReplacePlayerForConnection(conn, player, keepAuthority);
        }
        static public bool AddPlayerForConnection(NetworkConnection conn, GameObject player, Guid assetId)
        {
            return NetworkServer.AddPlayerForConnection(conn, player, assetId);
        }
        static public bool AddPlayerForConnection(NetworkConnection conn, GameObject player)
        {
            return NetworkServer.AddPlayerForConnection(conn, player);
        }
        static public void SetClientReady(NetworkConnection conn) { NetworkServer.SetClientReady(conn); }
        static public void SetAllClientsNotReady() { NetworkServer.SetAllClientsNotReady(); }
        static public void SetClientNotReady(NetworkConnection conn) { NetworkServer.SetClientNotReady(conn); }
        static public void DestroyPlayerForConnection(NetworkConnection conn) { NetworkServer.DestroyPlayerForConnection(conn); }
        static public void Spawn(GameObject obj) { NetworkServer.Spawn(obj); }
        static public void Spawn(GameObject obj, GameObject player) { NetworkServer.Spawn(obj, player); }
        static public void Spawn(GameObject obj, NetworkConnection conn) { NetworkServer.Spawn(obj, conn); }
        static public void Spawn(GameObject obj, Guid assetId, NetworkConnection conn) { NetworkServer.Spawn(obj, assetId, conn); }
        static public void Spawn(GameObject obj, Guid assetId) { NetworkServer.Spawn(obj, assetId); }
        static public void Destroy(GameObject obj) { NetworkServer.Destroy(obj); }
        static public void UnSpawn(GameObject obj) { NetworkServer.UnSpawn(obj); }
        static public NetworkIdentity FindLocalObject(uint netId) { return NetworkIdentity.spawned[netId]; }
        static public bool SpawnObjects() { return NetworkServer.SpawnObjects(); }

        #endregion
    }
}
