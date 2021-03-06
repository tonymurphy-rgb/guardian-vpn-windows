﻿// <copyright file="Tunnel.cs" company="Mozilla">
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed with this file, you can obtain one at http://mozilla.org/MPL/2.0/.
// </copyright>

using System;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;

namespace FirefoxPrivateNetwork.WireGuard
{
    /// <summary>
    /// WireGuard Tunnel class, used for housing connection data and initiating the tunnel connection.
    /// </summary>
    internal class Tunnel
    {
        private WireGuard.IPC brokerIPC;

        private long previousTxQueryTime;
        private long previousTxBytes;

        private DateTime uptimeStart = DateTime.MinValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="Tunnel"/> class.
        /// </summary>
        /// <param name="broker">Instantiated broker object.</param>
        public Tunnel(Broker broker)
        {
            Broker = broker;
        }

        /// <summary>
        /// Gets the currently used Broker instance.
        /// </summary>
        public WireGuard.Broker Broker { get; private set; }

        /// <summary>
        /// Gets a value indicating whether we are connecting or not.
        /// </summary>
        public bool IsConnecting { get; private set; }

        /// <summary>
        /// Gets a value indicating whether we are disconnecting or not.
        /// </summary>
        public bool IsDisconnecting { get; private set; }

        /// <summary>
        /// Main WireGuard Tunnel Service endpoint within tunnel.dll, calling this will initiate a WireGuard service.
        /// </summary>
        /// <param name="configurationFilename">Path to the filename which will be used for this tunnel instance.</param>
        /// <returns>Returns true when the service completes successfully.</returns>
        [DllImport("tunnel.dll", EntryPoint = "WireGuardTunnelService", CallingConvention = CallingConvention.Cdecl)]
        public static extern bool WireGuardTunnelService([MarshalAs(UnmanagedType.LPWStr)] string configurationFilename);

        /// <summary>
        /// TunnelService thread, harnesses Tunnel.dll and initiates the WireGuard tunneling service.
        /// Called from Main.cs when the exe is run with the "tunnel" parameter.
        /// </summary>
        /// <param name="confFilePath">Path to the WireGuard config file to use, containing keys, IPs and everything else.</param>
        /// <returns>True on successful startup of the tunnel service, false otherwise.</returns>
        public bool TunnelService(string confFilePath)
        {
            try
            {
                ErrorHandling.DebugLogger.LogDebugMsg("Attempting to start tunnel service");
                return WireGuardTunnelService(confFilePath);
            }
            catch (ExternalException e)
            {
                ErrorHandling.ErrorHandler.Handle(e, ErrorHandling.LogLevel.Error);
            }

            return false;
        }

        /// <summary>
        /// Saves a config file to the users' AppData folder and sends a connect command to the Broker.
        /// </summary>
        /// <returns>True if successfully sent connection command.</returns>
        public bool Connect()
        {
            uptimeStart = DateTime.MinValue;
            SetConnecting();
            Manager.Broker.ReportHeartbeat();

            ErrorHandling.DebugLogger.LogDebugMsg("Starting broker");
            if (!InitiateBroker())
            {
                IsConnecting = false;
                IsDisconnecting = false;
                return false;
            }

            var configFilePath = ProductConstants.FirefoxPrivateNetworkConfFile;
            var connectMessage = new IPCMessage(IPCCommand.IpcConnect);
            connectMessage.AddAttribute("config", configFilePath);
            brokerIPC.WriteToPipe(connectMessage);

            return true;
        }

        /// <summary>
        /// Sends a disconnect command to the Broker.
        /// </summary>
        /// <param name="runBroker">Creates a new broker instance if there isn't one already available.</param>
        /// <returns>True if successfully sent disconnection command.</returns>
        public bool Disconnect(bool runBroker = true)
        {
            uptimeStart = DateTime.MinValue;
            SetDisconnecting();
            Manager.Broker.ReportHeartbeat();

            if (!runBroker && !Broker.IsActive())
            {
                ClearConnectionTransitionState();
                return false;
            }

            if (!InitiateBroker())
            {
                ClearConnectionTransitionState();
                return false;
            }

            var connectMessage = new IPCMessage(IPCCommand.IpcDisconnect);
            brokerIPC.WriteToPipe(connectMessage);

            return true;
        }

        /// <summary>
        /// Switches the VPN server to a different endpoint.
        /// </summary>
        /// <param name="endpoint">Endpoint (VPN server) IP address.</param>
        /// <param name="publicKey">Public key of remote VPN server.</param>
        /// <returns>True if successfully sent the switch command.</returns>
        public bool SwitchServer(string endpoint, string publicKey)
        {
            NamedPipeClientStream tunnelPipe = null;
            try
            {
                using (tunnelPipe = ConnectWGTunnelNamedPipe())
                {
                    if (tunnelPipe == null)
                    {
                        return false;
                    }

                    var serverSwitchRequest = new WireGuard.IPCMessage(WireGuard.IPCCommand.WgSet);
                    serverSwitchRequest.AddAttribute("replace_peers", "true");
                    serverSwitchRequest.AddAttribute("public_key", BitConverter.ToString(Convert.FromBase64String(publicKey)).Replace("-", string.Empty).ToLower());
                    serverSwitchRequest.AddAttribute("endpoint", endpoint);

                    var allowedIPs = ProductConstants.AllowedIPs.Split(',').Select(ip => ip.Trim()).ToList();
                    allowedIPs.ForEach(ip => serverSwitchRequest.AddAttribute("allowed_ip", ip));

                    IPC.WriteToPipe(tunnelPipe, serverSwitchRequest);

                    var response = IPC.ReadFromPipe(tunnelPipe);
                    var errno = response["errno"].FirstOrDefault();
                    if (errno == null || errno != "0")
                    {
                        throw new Exception("Set request UAPI error " + errno);
                    }
                }
            }
            catch (Exception e)
            {
                ErrorHandling.ErrorHandler.Handle(e, ErrorHandling.LogLevel.Error);
                return false;
            }
            finally
            {
                if (tunnelPipe != null && tunnelPipe.IsConnected)
                {
                    tunnelPipe.Close();
                }
            }

            return true;
        }

        /// <summary>
        /// Fetches the connection timestamp.
        /// </summary>
        /// <returns>Object depicting the time when the connection was first made.</returns>
        public DateTime GetConnectionTime()
        {
            return uptimeStart;
        }

        /// <summary>
        /// Retrieves the current connection status of the Tunnel.
        /// </summary>
        /// <returns>Object depicting the current status.</returns>
        public Models.ConnectionStatus ConnectionStatus()
        {
            // Broker process checks
            if (!Broker.IsActive())
            {
                if (IsConnecting == false && IsDisconnecting == false)
                {
                    // Not connecting, not disconnecting, tunnel is not up and user is unprotected
                    return new Models.ConnectionStatus() { Status = Models.ConnectionState.Unprotected };
                }
                else if (IsConnecting == true)
                {
                    // We are currently only connecting
                    return new Models.ConnectionStatus() { Status = Models.ConnectionState.Connecting };
                }
                else if (IsDisconnecting == true)
                {
                    // We are currently only disconnecting
                    return new Models.ConnectionStatus() { Status = Models.ConnectionState.Disconnecting };
                }
            }

            // Broker process is running, do tunnel service checks
            if (Service.IsTunnelServiceRunning())
            {
                // Service is now running, clear transitioning states and query statistics
                ClearConnectionTransitionState();
                return QueryConnectionStatisticsFromTunnel();
            }

            if (!Service.IsTunnelServiceRunning() && IsDisconnecting)
            {
                // Service is not running, we should clear the connection transition state as the disconnection is now complete
                ClearConnectionTransitionState();
            }

            // Broker process is running, but the tunnel service is not running
            if (IsConnecting == false)
            {
                // We are not in a connecting state, meaning if the tunnel service is down, the user is unprotected
                return new Models.ConnectionStatus() { Status = Models.ConnectionState.Unprotected };
            }
            else if (IsConnecting == true)
            {
                // We are currently only connecting
                return new Models.ConnectionStatus() { Status = Models.ConnectionState.Connecting };
            }
            else if (IsDisconnecting == true)
            {
                // We are currently only disconnecting
                return new Models.ConnectionStatus() { Status = Models.ConnectionState.Disconnecting };
            }

            // No previous checks apply, the user is unprotected
            return new Models.ConnectionStatus() { Status = Models.ConnectionState.Unprotected };
        }

        /// <summary>
        /// Send a request for connection statistics from the tunnel service via named pipe.
        /// </summary>
        /// <returns>Connection status received from the tunnel named pipe.</returns>
        private Models.ConnectionStatus QueryConnectionStatisticsFromTunnel()
        {
            NamedPipeClientStream tunnelPipe = null;
            try
            {
                tunnelPipe = ConnectWGTunnelNamedPipe();
                if (tunnelPipe != null)
                {
                    IPC.WriteToPipe(tunnelPipe, new WireGuard.IPCMessage(WireGuard.IPCCommand.WgGet));
                    var ret = ParseStatusResponse(IPC.ReadFromPipe(tunnelPipe));
                    tunnelPipe.Close();
                    return ret;
                }
            }
            catch (Exception e)
            {
                ErrorHandling.ErrorHandler.Handle(e, ErrorHandling.LogLevel.Debug);
            }
            finally
            {
                if (tunnelPipe != null && tunnelPipe.IsConnected)
                {
                    tunnelPipe.Close();
                }
            }

            return new Models.ConnectionStatus() { Status = Models.ConnectionState.Protected, ConnectionStability = Models.ConnectionStability.NoSignal };
        }

        private Models.ConnectionStatus ParseStatusResponse(IPCMessage message)
        {
            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var connectionStatus = new Models.ConnectionStatus
            {
                PrivateKey = message["private_key"].FirstOrDefault(),
                ListenPort = message["listen_port"].FirstOrDefault(),
                PublicKey = message["public_key"].FirstOrDefault(),
                PresharedKey = message["preshared_key"].FirstOrDefault(),
                ProtocolVersion = message["protocol_version"].FirstOrDefault(),
                Endpoint = message["endpoint"].FirstOrDefault(),
                LastHandshakeTimeSec = message["last_handshake_time_sec"].FirstOrDefault(),
                LastHandshakeTimeNsec = message["last_handshake_time_nsec"].FirstOrDefault(),
                TxBytes = message["tx_bytes"].FirstOrDefault(),
                RxBytes = message["rx_bytes"].FirstOrDefault(),
                PersistenKeepaliveInterval = message["persistent_keepalive_interval"].FirstOrDefault(),
                AllowedIp = string.Join(", ", message["allowed_ip"]),
                ErrNo = message["errno"].FirstOrDefault(),
                ConnectionStability = Models.ConnectionStability.Stable,
            };

            // Set new uptime
            if (uptimeStart == DateTime.MinValue)
            {
                uptimeStart = DateTime.Now;
            }

            // Check whether there has been any data received in the past X seconds
            long.TryParse(connectionStatus.RxBytes, out long currentTxBytes);
            if (currentTxBytes - previousTxBytes != 0)
            {
                previousTxQueryTime = currentTime;
            }

            if (DateTime.Now.Subtract(uptimeStart).TotalSeconds > ProductConstants.TunnelInitialGracePeriodTimeout)
            {
                long.TryParse(connectionStatus.LastHandshakeTimeSec, out long lastHandshakeTime);

                var connectionStability = Models.ConnectionStability.Stable;

                if (currentTime - previousTxQueryTime > ProductConstants.TunnelUnstableTimeout)
                {
                    connectionStability = Models.ConnectionStability.Unstable;
                }

                // Check when was the last time the handshake has been received
                if (connectionStability == Models.ConnectionStability.Unstable && currentTime - lastHandshakeTime > ProductConstants.TunnelNoSignalTimeout)
                {
                    connectionStability = Models.ConnectionStability.NoSignal;
                }

                connectionStatus.ConnectionStability = connectionStability;
            }

            previousTxBytes = currentTxBytes;

            // Mark connection as protected, because the named pipe to WireGuard is up
            connectionStatus.Status = Models.ConnectionState.Protected;

            return connectionStatus;
        }

        private NamedPipeClientStream ConnectWGTunnelNamedPipe()
        {
            try
            {
                var tunnelPipe = new NamedPipeClientStream(ProductConstants.TunnelPipeName);
                tunnelPipe.Connect(1000);
                return tunnelPipe;
            }
            catch (TimeoutException)
            {
                if (Manager.MainWindowViewModel.Status == Models.ConnectionState.Protected)
                {
                    ErrorHandling.ErrorHandler.Handle("Named pipe not available", ErrorHandling.LogLevel.Debug);
                }

                return null;
            }
        }

        private void SetConnecting()
        {
            IsConnecting = true;
            IsDisconnecting = false;
            Manager.MainWindowViewModel.IsServerSwitching = false;
        }

        private void SetDisconnecting()
        {
            IsConnecting = false;
            IsDisconnecting = true;
            Manager.MainWindowViewModel.IsServerSwitching = false;
        }

        private void ClearConnectionTransitionState()
        {
            IsConnecting = false;
            IsDisconnecting = false;
        }

        /// <summary>
        /// Launch the elevated child process, if not already running and start an IPC listener thread.
        /// </summary>
        /// <returns>True if success, false otherwise.</returns>
        private bool InitiateBroker()
        {
            if (!Broker.IsActive())
            {
                if (!Broker.LaunchChildProcess())
                {
                    return false;
                }
            }

            brokerIPC = Manager.Broker.GetBrokerIPC();
            return true;
        }
    }
}
