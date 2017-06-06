﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices.Client
{
    using System;
#if !PCL
    using System.Net.Security;
#endif
    using System.Threading;
    using System.Threading.Tasks;

#if !WINDOWS_UWP && !PCL
#if !NETSTANDARD1_3
    using System.Configuration;
#endif
    using System.Net.WebSockets;
    using System.Security.Cryptography.X509Certificates;
#endif
    using System.Net;
    using Microsoft.Azure.Amqp;
    using Microsoft.Azure.Amqp.Framing;
    using Microsoft.Azure.Amqp.Transport;
    using Microsoft.Azure.Devices.Client.Extensions;
    using Microsoft.Azure.Devices.Client.Transport;

    abstract class IotHubConnection
    {
        readonly string hostName;
        readonly int port;

        static readonly AmqpVersion AmqpVersion_1_0_0 = new AmqpVersion(1, 0, 0);

        const string DisableServerCertificateValidationKeyName =
            "Microsoft.Azure.Devices.DisableServerCertificateValidation";

        static readonly Lazy<bool> DisableServerCertificateValidation =
            new Lazy<bool>(InitializeDisableServerCertificateValidation);

        private SemaphoreSlim sessionSemaphore = new SemaphoreSlim(1, 1);

        protected IotHubConnection(string hostName, int port, AmqpTransportSettings amqpTransportSettings)
        {
            this.hostName = hostName;
            this.port = port;
            this.AmqpTransportSettings = amqpTransportSettings;
        }

        protected FaultTolerantAmqpObject<AmqpSession> FaultTolerantSession { get; set; }

        protected AmqpTransportSettings AmqpTransportSettings { get; }

        public abstract Task CloseAsync();

        public abstract void SafeClose(Exception exception);

        public async Task<SendingAmqpLink> CreateSendingLinkAsync(
            string path,
            IotHubConnectionString connectionString,
            AmqpLinkSettings linkSettings,
            TimeSpan timeout, 
            CancellationToken cancellationToken)
        {
            this.OnCreateSendingLink(connectionString);

            var timeoutHelper = new TimeoutHelper(timeout);

            AmqpSession session = await this.GetSessionAsync(timeoutHelper, cancellationToken);

            var link = new SendingAmqpLink(linkSettings);
            link.AttachTo(session);

            var audience = this.BuildAudience(connectionString, path);
            await this.OpenLinkAsync(link, connectionString, audience, timeoutHelper.RemainingTime(), cancellationToken);

            return link;
        }

        public async Task<ReceivingAmqpLink> CreateReceivingLinkAsync(
            string path,
            IotHubConnectionString connectionString,
            AmqpLinkSettings linkSettings,
            TimeSpan timeout,
            uint prefetchCount,
            CancellationToken cancellationToken)
        {
            this.OnCreateReceivingLink(connectionString);

            var timeoutHelper = new TimeoutHelper(timeout);

            AmqpSession session = await this.GetSessionAsync(timeoutHelper, cancellationToken);

            var link = new ReceivingAmqpLink(linkSettings);
            link.AttachTo(session);
 
            var audience = this.BuildAudience(connectionString, path);
            await this.OpenLinkAsync(link, connectionString, audience, timeoutHelper.RemainingTime(), cancellationToken);

            return link;
        }

        private async Task<AmqpSession> GetSessionAsync(TimeoutHelper timeoutHelper, CancellationToken token)
        {
            AmqpSession session;
            try
            {
                await sessionSemaphore.WaitAsync();

                session = await this.FaultTolerantSession.GetOrCreateAsync(timeoutHelper.RemainingTime(), token);

                Fx.Assert(session != null, "Amqp Session cannot be null.");
                if (session.State != AmqpObjectState.Opened)
                {                    
                    if (session.State == AmqpObjectState.End)
                    {
                        this.FaultTolerantSession.TryRemove();
                    }
                    session = await this.FaultTolerantSession.GetOrCreateAsync(timeoutHelper.RemainingTime(), token);
                }
            }
            finally
            {
                sessionSemaphore.Release();
            }

            return session;
        }

        public void CloseLink(AmqpLink link)
        {
            link.SafeClose();
        }

        public abstract void Release(string deviceId);

        internal abstract Uri BuildLinkAddress(IotHubConnectionString iotHubConnectionString, string path);

        protected abstract string BuildAudience(IotHubConnectionString iotHubConnectionString, string path);

        protected abstract Task OpenLinkAsync(AmqpObject link, IotHubConnectionString connectionString, string audience, TimeSpan timeout, CancellationToken cancellationToken);

        protected static bool InitializeDisableServerCertificateValidation()
        {
#if PCL
            return false;
#else
#if WINDOWS_UWP || NETSTANDARD1_3 // No System.Configuration.ConfigurationManager in UWP/PCL, NetStandard
            bool flag;
            if (!AppContext.TryGetSwitch("DisableServerCertificateValidationKeyName", out flag))
            {
                return false;
            }
            return flag;
#else
            string value = ConfigurationManager.AppSettings[DisableServerCertificateValidationKeyName];
            if (!string.IsNullOrEmpty(value))
            {
                return bool.Parse(value);
            }
            return false;
#endif
#endif
        }

        protected virtual void OnCreateSendingLink(IotHubConnectionString connectionString)
        {
            // do nothing. Override in derived classes if necessary
        }

        protected virtual void OnCreateReceivingLink(IotHubConnectionString connectionString)
        {
            // do nothing. Override in derived classes if necessary
        }

        protected virtual async Task<AmqpSession> CreateSessionAsync(TimeSpan timeout, CancellationToken token)
        {
            this.OnCreateSession();

            var timeoutHelper = new TimeoutHelper(timeout);

            AmqpSettings amqpSettings = CreateAmqpSettings();
            TransportBase transport;

            token.ThrowIfCancellationRequested();

            switch (this.AmqpTransportSettings.GetTransportType())
            {
#if !WINDOWS_UWP && !PCL
                case TransportType.Amqp_WebSocket_Only:
                    transport = await this.CreateClientWebSocketTransportAsync(timeoutHelper.RemainingTime());
                    break;
#endif
                case TransportType.Amqp_Tcp_Only:
                    TlsTransportSettings tlsTransportSettings = this.CreateTlsTransportSettings();
                    var amqpTransportInitiator = new AmqpTransportInitiator(amqpSettings, tlsTransportSettings);
                    transport = await amqpTransportInitiator.ConnectTaskAsync(timeoutHelper.RemainingTime());
                    break;
                default:
                    throw new InvalidOperationException("AmqpTransportSettings must specify WebSocketOnly or TcpOnly");
            }

            var amqpConnectionSettings = new AmqpConnectionSettings()
            {
                MaxFrameSize = AmqpConstants.DefaultMaxFrameSize,
                ContainerId = Guid.NewGuid().ToString("N"),
                HostName = this.hostName
            };

            var amqpConnection = new AmqpConnection(transport, amqpSettings, amqpConnectionSettings);
            try
            {
                token.ThrowIfCancellationRequested();
                await amqpConnection.OpenAsync(timeoutHelper.RemainingTime());

                var sessionSettings = new AmqpSessionSettings()
                {
                    Properties = new Fields()
                };

                AmqpSession amqpSession = amqpConnection.CreateSession(sessionSettings);
                token.ThrowIfCancellationRequested();
                await amqpSession.OpenAsync(timeoutHelper.RemainingTime());

                // This adds itself to amqpConnection.Extensions
                var cbsLink = new AmqpCbsLink(amqpConnection);
                return amqpSession;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                if (amqpConnection.TerminalException != null)
                {
                    throw AmqpClientHelper.ToIotHubClientContract(amqpConnection.TerminalException);
                }

                amqpConnection.SafeClose(ex);
                throw;
            }
        }

        protected virtual void OnCreateSession()
        {
            // do nothing. Override in derived classes if necessary
        }

#if !WINDOWS_UWP && !PCL
        async Task<ClientWebSocket> CreateClientWebSocketAsync(Uri websocketUri, TimeSpan timeout)
        {
            var websocket = new ClientWebSocket();

            // Set SubProtocol to AMQPWSB10
            websocket.Options.AddSubProtocol(WebSocketConstants.SubProtocols.Amqpwsb10);

            // Check if we're configured to use a proxy server
            IWebProxy webProxy = WebRequest.DefaultWebProxy;
            Uri proxyAddress = webProxy != null ? webProxy.GetProxy(websocketUri) : null;
            if (!websocketUri.Equals(proxyAddress))
            {
                // Configure proxy server
                websocket.Options.Proxy = webProxy;
            }

            if (this.AmqpTransportSettings.ClientCertificate != null)
            {
                websocket.Options.ClientCertificates.Add(this.AmqpTransportSettings.ClientCertificate);
            }
#if !NETSTANDARD1_3
            else
            {
                websocket.Options.UseDefaultCredentials = true;
            }
#endif

            using (var cancellationTokenSource = new CancellationTokenSource(timeout))
            {
                await websocket.ConnectAsync(websocketUri, cancellationTokenSource.Token);
            }

            return websocket;
        }

        async Task<TransportBase> CreateClientWebSocketTransportAsync(TimeSpan timeout)
        {
            var timeoutHelper = new TimeoutHelper(timeout);
            Uri websocketUri = new Uri(WebSocketConstants.Scheme + this.hostName + ":" + WebSocketConstants.SecurePort + WebSocketConstants.UriSuffix);
            // Use Legacy WebSocket if it is running on Windows 7 or older. Windows 7/Windows 2008 R2 is version 6.1
#if !NETSTANDARD1_3
            if (Environment.OSVersion.Version.Major < 6 || (Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor <= 1))
            {
                var websocket = await CreateLegacyClientWebSocketAsync(websocketUri, this.AmqpTransportSettings.ClientCertificate, timeoutHelper.RemainingTime());
                return new LegacyClientWebSocketTransport(
                    websocket,
                    this.AmqpTransportSettings.OperationTimeout,
                    null,
                    null);
            }
            else
            {
#endif
                var websocket = await this.CreateClientWebSocketAsync(websocketUri, timeoutHelper.RemainingTime());
                return new ClientWebSocketTransport(
                    websocket,
                    null,
                    null);
#if !NETSTANDARD1_3
            }
#endif
        }

        static async Task<IotHubClientWebSocket> CreateLegacyClientWebSocketAsync(Uri webSocketUri, X509Certificate2 clientCertificate, TimeSpan timeout)
        {
            var websocket = new IotHubClientWebSocket(WebSocketConstants.SubProtocols.Amqpwsb10);
            await websocket.ConnectAsync(webSocketUri.Host, webSocketUri.Port, WebSocketConstants.Scheme, clientCertificate, timeout);
            return websocket;
        }
#endif

        static AmqpSettings CreateAmqpSettings()
        {
            var amqpSettings = new AmqpSettings();

            var amqpTransportProvider = new AmqpTransportProvider();
            amqpTransportProvider.Versions.Add(AmqpVersion_1_0_0);
            amqpSettings.TransportProviders.Add(amqpTransportProvider);

            return amqpSettings;
        }

        TlsTransportSettings CreateTlsTransportSettings()
        {
            var tcpTransportSettings = new TcpTransportSettings()
            {
                Host = this.hostName,
                Port = this.port
            };

            var tlsTransportSettings = new TlsTransportSettings(tcpTransportSettings)
            {
                TargetHost = this.hostName,
#if !WINDOWS_UWP && !PCL // Not supported in UWP/PCL
                Certificate = null,
                CertificateValidationCallback = OnRemoteCertificateValidation
#endif
            };

#if !WINDOWS_UWP && !PCL
            if (this.AmqpTransportSettings.ClientCertificate != null)
            {
                tlsTransportSettings.Certificate = this.AmqpTransportSettings.ClientCertificate;
            }
#endif

            return tlsTransportSettings;
        }

#if !WINDOWS_UWP && !PCL // Not supported in UWP/PCL
        public static bool OnRemoteCertificateValidation(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            if (DisableServerCertificateValidation.Value && sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch)
            {
                return true;
            }

            return false;
        }
#endif

        public static ArraySegment<byte> GetNextDeliveryTag(ref int deliveryTag)
        {
            int nextDeliveryTag = Interlocked.Increment(ref deliveryTag);
            return new ArraySegment<byte>(BitConverter.GetBytes(nextDeliveryTag));
        }

        public static ArraySegment<byte> ConvertToDeliveryTag(string lockToken)
        {
            if (lockToken == null)
            {
                throw new ArgumentNullException("lockToken");
            }

            Guid lockTokenGuid;
            if (!Guid.TryParse(lockToken, out lockTokenGuid))
            {
                throw new ArgumentException("Should be a valid Guid", "lockToken");
            }

            var deliveryTag = new ArraySegment<byte>(lockTokenGuid.ToByteArray());
            return deliveryTag;
        }
    }
}
