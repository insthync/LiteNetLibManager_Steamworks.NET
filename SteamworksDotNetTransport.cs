using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using Steamworks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SteamworksDotNetTransportLayer
{
    public class SteamworksDotNetTransport : ITransport
    {
        private class SteamConnectionData
        {
            internal SteamConnectionData(CSteamID steamId, HSteamNetConnection connection)
            {
                this.steamId = steamId;
                this.connection = connection;
            }

            internal CSteamID steamId;
            internal HSteamNetConnection connection;
        }

        private const int MAX_MESSAGES = 256;
        private const string LOCAL_HOST = "localhost";
        private const string LOCAL_IP = "127.0.0.1";

        public int ServerPeersCount => _serverPeers.Count;

        public int ServerMaxConnections { get; private set; }

        public bool IsClientStarted => _client != null;

        public bool IsServerStarted { get; private set; }

        public bool HasImplementedPing => false;

        private SteamConnectionData _client = null;
        private HSteamListenSocket _serverSocket;
        private readonly Dictionary<long, SteamConnectionData> _serverPeers = new Dictionary<long, SteamConnectionData>();
        private readonly Queue<TransportEventData> _clientEventQueue = new Queue<TransportEventData>();
        private readonly Queue<TransportEventData> _serverEventQueue = new Queue<TransportEventData>();
        private Callback<SteamNetConnectionStatusChangedCallback_t> _onConnectionChanged = null;
        private SteamNetworkingConfigValue_t[] _options = new SteamNetworkingConfigValue_t[0];

        public SteamworksDotNetTransport()
        {

        }

        public long GetClientRtt()
        {
            return 0;
        }

        public long GetServerRtt(long connectionId)
        {
            return 0;
        }

        public bool ClientReceive(out TransportEventData eventData)
        {
            eventData = default;
            if (_client == null)
                return false;
            ReceiveMessage(_client, _clientEventQueue);
            if (_clientEventQueue.Count == 0)
                return false;
            eventData = _clientEventQueue.Dequeue();
            return true;
        }

        public bool ServerReceive(out TransportEventData eventData)
        {
            eventData = default;
            if (!IsServerStarted)
                return false;
            foreach (SteamConnectionData peer in _serverPeers.Values)
            {
                ReceiveMessage(peer, _serverEventQueue);
            }
            if (_serverEventQueue.Count == 0)
                return false;
            eventData = _serverEventQueue.Dequeue();
            return true;
        }

        public bool ClientSend(byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            return SendMessage(deliveryMethod, writer, _client.connection);
        }

        public bool ServerSend(long connectionId, byte dataChannel, DeliveryMethod deliveryMethod, NetDataWriter writer)
        {
            if (IsServerStarted && _serverPeers.TryGetValue(connectionId, out SteamConnectionData peer))
            {
                return SendMessage(deliveryMethod, writer, peer.connection);
            }
            // No client found so report that
            Debug.LogError($"Trying to send on unknown connection: {connectionId}");
            return false;
        }

        public bool StartClient(string address, int port)
        {
            try
            {
                SteamNetworkingUtils.InitRelayNetworkAccess();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Exception: {ex.Message}. relay network access cannot be created when start client.");
                return false;
            }

            if (string.Equals(address, LOCAL_HOST) || string.Equals(address, LOCAL_IP))
            {
                CSteamID steamId = SteamUser.GetSteamID();
                return StartClient(steamId.m_SteamID.ToString(), port);
            }

            ulong connectToSteamId = ulong.Parse(address);

            if (_onConnectionChanged == null)
            {
                _onConnectionChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            }

            try
            {
                CSteamID steamId = new CSteamID(connectToSteamId);
                SteamNetworkingIdentity serverIdentity = new SteamNetworkingIdentity();
                serverIdentity.SetSteamID(steamId);
                _client = new SteamConnectionData(steamId, SteamNetworkingSockets.ConnectP2P(ref serverIdentity, port, _options.Length, _options));
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Exception: {ex.Message}. Client could not be started.");
                _client = null;
                return false;
            }
        }

        public bool StartServer(int port, int maxConnections)
        {
            ServerMaxConnections = maxConnections;

            if (_onConnectionChanged == null)
            {
                _onConnectionChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            }

            try
            {
                SteamNetworkingUtils.InitRelayNetworkAccess();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Exception: {ex.Message}. relay network access cannot be created when start server.");
            }

            _serverSocket = SteamNetworkingSockets.CreateListenSocketP2P(port, _options.Length, _options);
            IsServerStarted = true;
            return true;
        }

        public void StopClient()
        {
            if (!IsServerStarted)
            {
                // Don't dispose callback, it will be disposed later (in StopServer function)
                if (_onConnectionChanged != null)
                {
                    _onConnectionChanged.Dispose();
                    _onConnectionChanged = null;
                }
            }
            if (_client != null)
                SteamNetworkingSockets.CloseConnection(_client.connection, 0, "Disconnected", false);
            _client = null;
        }

        public void StopServer()
        {
            if (_onConnectionChanged != null)
            {
                _onConnectionChanged.Dispose();
                _onConnectionChanged = null;
            }
            foreach (SteamConnectionData peer in _serverPeers.Values)
            {
                SteamNetworkingSockets.CloseConnection(peer.connection, 0, "Server stopped", false);
            }
            IsServerStarted = false;
            _serverPeers.Clear();
            SteamNetworkingSockets.CloseListenSocket(_serverSocket);
        }

        public bool ServerDisconnect(long connectionId)
        {
            if (!_serverPeers.ContainsKey(connectionId))
            {
                Debug.LogError($"Can't disconnect client, client not connected, connectionId: {connectionId}");
                return false;
            }
            ServerDisconnectInternal(connectionId);
            return true;
        }

        private void ServerDisconnectInternal(long connectionId, string reasons = "Disconnected")
        {
            if (!_serverPeers.ContainsKey(connectionId))
                return;
            SteamNetworkingSockets.CloseConnection(_serverPeers[connectionId].connection, 0, reasons, false);
            _serverPeers.Remove(connectionId);
        }

        public void Destroy()
        {
            StopClient();
            StopServer();
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t callback)
        {
            Debug.Log($"OnConnectionStatusChanged {callback.m_info.m_eState}");
            CSteamID remoteSteamId = callback.m_info.m_identityRemote.GetSteamID();
            switch (callback.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    if (IsServerStarted)
                    {
                        EResult acceptResult = SteamNetworkingSockets.AcceptConnection(callback.m_hConn);
                        if (acceptResult != EResult.k_EResultOK)
                        {
                            Debug.LogWarning($"Cannot accept {callback.m_info.m_identityRemote.GetSteamID64()}: {acceptResult}");
                        }
                        else
                        {
                            // Accept new connection
                            _serverPeers.Add((long)remoteSteamId.m_SteamID, new SteamConnectionData(remoteSteamId, callback.m_hConn));
                            _serverEventQueue.Enqueue(new TransportEventData()
                            {
                                connectionId = (long)remoteSteamId.m_SteamID,
                                type = ENetworkEvent.ConnectEvent,
                            });
                        }
                    }
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (IsClientStarted)
                    {
                        _clientEventQueue.Enqueue(new TransportEventData()
                        {
                            type = ENetworkEvent.ConnectEvent,
                        });
                    }
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                    if (IsClientStarted && _client.steamId.m_SteamID == remoteSteamId.m_SteamID)
                    {
                        _clientEventQueue.Enqueue(new TransportEventData()
                        {
                            type = ENetworkEvent.DisconnectEvent,
                            disconnectInfo = new DisconnectInfo()
                            {
                                Reason = DisconnectReason.DisconnectPeerCalled,
                            },
                        });
                    }
                    if (IsServerStarted && _serverPeers.ContainsKey((long)remoteSteamId.m_SteamID))
                    {
                        _serverEventQueue.Enqueue(new TransportEventData()
                        {
                            connectionId = (long)remoteSteamId.m_SteamID,
                            type = ENetworkEvent.DisconnectEvent,
                            disconnectInfo = new DisconnectInfo()
                            {
                                Reason = DisconnectReason.DisconnectPeerCalled,
                            },
                        });
                        ServerDisconnectInternal((long)remoteSteamId.m_SteamID, "Closed by peer");
                    }
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    if (IsClientStarted && _client.steamId.m_SteamID == remoteSteamId.m_SteamID)
                    {
                        _clientEventQueue.Enqueue(new TransportEventData()
                        {
                            type = ENetworkEvent.DisconnectEvent,
                            disconnectInfo = new DisconnectInfo()
                            {
                                Reason = DisconnectReason.ConnectionFailed,
                            },
                        });
                    }
                    if (IsServerStarted && _serverPeers.ContainsKey((long)remoteSteamId.m_SteamID))
                    {
                        _serverEventQueue.Enqueue(new TransportEventData()
                        {
                            connectionId = (long)remoteSteamId.m_SteamID,
                            type = ENetworkEvent.DisconnectEvent,
                            disconnectInfo = new DisconnectInfo()
                            {
                                Reason = DisconnectReason.ConnectionFailed,
                            },
                        });
                        ServerDisconnectInternal((long)remoteSteamId.m_SteamID, "Problem detected");
                    }
                    break;
                default:
                    Debug.Log($"{remoteSteamId}'s connection state changed: {callback.m_info.m_eState}");
                    break;
            }
        }

        private void ReceiveMessage(SteamConnectionData peer, Queue<TransportEventData> eventQueue)
        {

            System.IntPtr[] ptrs = new System.IntPtr[MAX_MESSAGES];
            int messageCount = SteamNetworkingSockets.ReceiveMessagesOnConnection(peer.connection, ptrs, MAX_MESSAGES);
            if (messageCount > 0)
            {
                for (int i = 0; i < messageCount; ++i)
                {
                    SteamNetworkingMessage_t data = Marshal.PtrToStructure<SteamNetworkingMessage_t>(ptrs[i]);

                    if (data.m_cbSize > 0)
                    {
                        byte[] buffer = new byte[data.m_cbSize - 1];
                        Marshal.Copy(data.m_pData, buffer, 0, data.m_cbSize - 1);
                        eventQueue.Enqueue(new TransportEventData()
                        {
                            connectionId = (long)peer.steamId.m_SteamID,
                            type = ENetworkEvent.DataEvent,
                            reader = new NetDataReader(buffer),
                        });
                        SteamNetworkingMessage_t.Release(ptrs[i]);
                    }
                }
            }
        }

        private bool SendMessage(DeliveryMethod deliveryMethod, NetDataWriter writer, HSteamNetConnection connection)
        {
            // Prepare data to send, not sure why data size must be +1 to make it read properly
            byte[] data = new byte[writer.Length + 1];
            System.Array.Copy(writer.Data, 0, data, 0, writer.Length);
            // Create an unmanaged array and get a pointer to it
            GCHandle pinnedArray = GCHandle.Alloc(data, GCHandleType.Pinned);
            System.IntPtr pData = pinnedArray.AddrOfPinnedObject();
            // Send the message
            EResult sendResult = SteamNetworkingSockets.SendMessageToConnection(connection, pData, (uint)data.Length, GetSendFlag(deliveryMethod), out long _);
            // Free the unmanaged array
            pinnedArray.Free();
            // Not OK?
            if (sendResult == EResult.k_EResultNoConnection || sendResult == EResult.k_EResultInvalidParam)
            {
                Debug.LogWarning("Connection to peer was lost");
                SteamNetworkingSockets.CloseConnection(connection, 0, "Disconnected", false);
                return false;
            }
            else if (sendResult != EResult.k_EResultOK)
            {
                Debug.LogError($"Cannot send: {sendResult}");
                return false;
            }
            return true;
        }

        private int GetSendFlag(DeliveryMethod deliveryMethod)
        {
            //Translate the DeliveryMethod to the Steam QoS send type .. we assume unreliable if nothing matches
            int sendFlag = Constants.k_nSteamNetworkingSend_Unreliable;
            switch (deliveryMethod)
            {
                case DeliveryMethod.ReliableOrdered:
                case DeliveryMethod.ReliableUnordered:
                case DeliveryMethod.ReliableSequenced:
                    sendFlag = Constants.k_nSteamNetworkingSend_Reliable;
                    break;
                case DeliveryMethod.Sequenced:
                    sendFlag = Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
                    break;
            }
            return sendFlag;
        }
    }
}