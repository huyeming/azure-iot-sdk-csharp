﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices.Client.Transport
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Amqp;
    using Microsoft.Azure.Amqp.Framing;
    using Microsoft.Azure.Devices.Client.Exceptions;
    using Microsoft.Azure.Devices.Client.Extensions;
    using Microsoft.Azure.Devices.Shared;

    sealed class AmqpTransportHandler : TransportHandler
    {
        static readonly IotHubConnectionCache TcpConnectionCache = new IotHubConnectionCache();
        static readonly IotHubConnectionCache WsConnectionCache = new IotHubConnectionCache();
        readonly string deviceId;
        readonly Client.FaultTolerantAmqpObject<SendingAmqpLink> faultTolerantEventSendingLink;
        readonly Client.FaultTolerantAmqpObject<ReceivingAmqpLink> faultTolerantDeviceBoundReceivingLink;
        volatile Client.FaultTolerantAmqpObject<SendingAmqpLink> faultTolerantMethodSendingLink;
        volatile Client.FaultTolerantAmqpObject<ReceivingAmqpLink> faultTolerantMethodReceivingLink;
        readonly IotHubConnectionString iotHubConnectionString;
        readonly TimeSpan openTimeout;
        readonly TimeSpan operationTimeout;
        readonly uint prefetchCount;

        Func<MethodRequestInternal, Task> messageListener;
        Action<object, EventArgs> linkClosedListener;
        Action<object, EventArgs> SafeAddClosedReceivingLinkHandler;
        Action<object, EventArgs> SafeAddClosedSendingLinkHandler;
        internal delegate void OnConnectionClosedDelegate(object sender, EventArgs e);

        int closed;

        internal AmqpTransportHandler(
            IPipelineContext context, IotHubConnectionString connectionString, 
            AmqpTransportSettings transportSettings,
            Action<object, EventArgs> onLinkClosedCallback,
            Func<MethodRequestInternal, Task> onMethodCallback = null)
            :base(context, transportSettings)
        {
            this.linkClosedListener = onLinkClosedCallback;

            TransportType transportType = transportSettings.GetTransportType();
            this.deviceId = connectionString.DeviceId;
            switch (transportType)
            {
                case TransportType.Amqp_Tcp_Only:
                    this.IotHubConnection = TcpConnectionCache.GetConnection(connectionString, transportSettings);
                    break;
                case TransportType.Amqp_WebSocket_Only:
                    this.IotHubConnection = WsConnectionCache.GetConnection(connectionString, transportSettings);
                    break;
                default:
                    throw new InvalidOperationException("Invalid Transport Type {0}".FormatInvariant(transportType));
            }

            this.openTimeout = transportSettings.OpenTimeout;
            this.operationTimeout = transportSettings.OperationTimeout;
            this.prefetchCount = transportSettings.PrefetchCount;
            this.faultTolerantEventSendingLink = new Client.FaultTolerantAmqpObject<SendingAmqpLink>(this.CreateEventSendingLinkAsync, this.IotHubConnection.CloseLink);
            this.faultTolerantDeviceBoundReceivingLink = new Client.FaultTolerantAmqpObject<ReceivingAmqpLink>(this.CreateDeviceBoundReceivingLinkAsync, this.IotHubConnection.CloseLink);
            this.iotHubConnectionString = connectionString;
            this.messageListener = onMethodCallback;
        }

        internal IotHubConnection IotHubConnection { get; }

        public override async Task OpenAsync(bool explicitOpen, CancellationToken cancellationToken)
        {
            if (!explicitOpen)
            {
                return;
            }

            await this.HandleTimeoutCancellation(async () =>
             {
                 try
                 {
                     await Task.WhenAll(
                         this.faultTolerantEventSendingLink.OpenAsync(this.openTimeout, cancellationToken),
                         this.faultTolerantDeviceBoundReceivingLink.OpenAsync(this.openTimeout, cancellationToken));
                 }
                 catch (Exception exception)
                 {
                     if (exception.IsFatal())
                     {
                         throw;
                     }

                     throw AmqpClientHelper.ToIotHubClientContract(exception);
                 }
             }, cancellationToken);
        }

        public override async Task SendEventAsync(Message message, CancellationToken cancellationToken)
        {
            await this.HandleTimeoutCancellation(async () =>
            {
                Outcome outcome;
                using (AmqpMessage amqpMessage = message.ToAmqpMessage())
                {
                    outcome = await this.SendAmqpMessageAsync(amqpMessage, cancellationToken);
                }

                if (outcome.DescriptorCode != Accepted.Code)
                {
                    throw AmqpErrorMapper.GetExceptionFromOutcome(outcome);
                }
            }, cancellationToken);
        }

        public override async Task SendEventAsync(IEnumerable<Message> messages, CancellationToken cancellationToken)
        {
            await this.HandleTimeoutCancellation(async () =>
            {
                // List to hold messages in Amqp friendly format
                var messageList = new List<Data>();

                foreach (Message message in messages)
                {
                    using (AmqpMessage amqpMessage = message.ToAmqpMessage())
                    {
                        var data = new Data()
                        {
                            Value = MessageConverter.ReadStream(amqpMessage.ToStream())
                        };
                        messageList.Add(data);
                    }
                }

                Outcome outcome;
                using (AmqpMessage amqpMessage = AmqpMessage.Create(messageList))
                {
                    amqpMessage.MessageFormat = AmqpConstants.AmqpBatchedMessageFormat;
                    outcome = await this.SendAmqpMessageAsync(amqpMessage, cancellationToken);
                }

                if (outcome.DescriptorCode != Accepted.Code)
                {
                    throw AmqpErrorMapper.GetExceptionFromOutcome(outcome);
                }
            }, cancellationToken);
        }

        public override async Task<Message> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Message message = null;

            await this.HandleTimeoutCancellation(async () =>
            {
                AmqpMessage amqpMessage;
                try
                {
                    ReceivingAmqpLink deviceBoundReceivingLink = await this.GetDeviceBoundReceivingLinkAsync(cancellationToken);
                    amqpMessage = await deviceBoundReceivingLink.ReceiveMessageAsync(timeout);
                }
                catch (Exception exception)
                {
                    if (exception.IsFatal())
                    {
                        throw;
                    }

                    throw AmqpClientHelper.ToIotHubClientContract(exception);
                }

                if (amqpMessage != null)
                {
                    message = new Message(amqpMessage)
                    {
                        LockToken = new Guid(amqpMessage.DeliveryTag.Array).ToString()
                    };
                }
                else
                {
                    message = null;
                }
            }, cancellationToken);

            return message;
        }

        public override Task RecoverConnections(object link, CancellationToken cancellationToken)
        {
#if WIP_C2D_METHODS_AMQP
            Func<Task> enableMethodLinkAsyncFunc = null;

            var amqpLink = link as AmqpLink;
            if (amqpLink == null)
            {
                return Common.TaskConstants.Completed;
            }

            if (amqpLink.IsReceiver)
            {
                this.faultTolerantMethodReceivingLink = new Client.FaultTolerantAmqpObject<ReceivingAmqpLink>(this.CreateMethodReceivingLinkAsync, this.IotHubConnection.CloseLink);
                enableMethodLinkAsyncFunc = async () => await EnableReceivingLinkAsync(cancellationToken);
            }
            else
            {
                this.faultTolerantMethodSendingLink = new Client.FaultTolerantAmqpObject<SendingAmqpLink>(this.CreateMethodSendingLinkAsync, this.IotHubConnection.CloseLink);
                enableMethodLinkAsyncFunc = async () => await EnableSendingLinkAsync(cancellationToken);
            }

            return this.HandleTimeoutCancellation(async () =>
            {
                try
                {
                    if (this.messageListener != null)
                    {
                        await enableMethodLinkAsyncFunc();
                    }
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    throw AmqpClientHelper.ToIotHubClientContract(ex);
                }
            }, cancellationToken);
#else
            throw new NotImplementedException();
#endif
        }

        public override Task EnableMethodsAsync(CancellationToken cancellationToken)
        {
#if WIP_C2D_METHODS_AMQP
            if (this.faultTolerantMethodSendingLink == null)
            {
                this.faultTolerantMethodSendingLink = new Client.FaultTolerantAmqpObject<SendingAmqpLink>(this.CreateMethodSendingLinkAsync, this.IotHubConnection.CloseLink);
            }

            if (this.faultTolerantMethodReceivingLink == null)
            {
                this.faultTolerantMethodReceivingLink = new Client.FaultTolerantAmqpObject<ReceivingAmqpLink>(this.CreateMethodReceivingLinkAsync, this.IotHubConnection.CloseLink);
            }

            return this.HandleTimeoutCancellation(async () =>
            {
                try
                {
                    if (this.messageListener != null)
                    {
                        await Task.WhenAll(EnableSendingLinkAsync(cancellationToken), EnableReceivingLinkAsync(cancellationToken));
                    }
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    throw AmqpClientHelper.ToIotHubClientContract(ex);
                }
            }, cancellationToken);
#else
            throw new NotImplementedException();
#endif
        }

#if WIP_C2D_METHODS_AMQP
        private async Task EnableSendingLinkAsync(CancellationToken cancellationToken)
        {
            SendingAmqpLink methodSendingLink = await this.GetMethodSendingLinkAsync(cancellationToken);
            this.SafeAddClosedSendingLinkHandler = this.linkClosedListener;
            methodSendingLink.SafeAddClosed((o, ea) => this.SafeAddClosedSendingLinkHandler(o, ea));
        }

        private async Task EnableReceivingLinkAsync(CancellationToken cancellationToken)
        {
            ReceivingAmqpLink methodReceivingLink = await this.GetMethodReceivingLinkAsync(cancellationToken);
            this.SafeAddClosedReceivingLinkHandler = this.linkClosedListener;
            methodReceivingLink.SafeAddClosed((o, ea) => this.SafeAddClosedReceivingLinkHandler(o, ea));
        }
#endif

        public override async Task DisableMethodsAsync(CancellationToken cancellationToken)
        {
#if WIP_C2D_METHODS_AMQP
            Task receivingLinkCloseTask;

            this.SafeAddClosedSendingLinkHandler = (o, ea) => {};
            this.SafeAddClosedReceivingLinkHandler = (o, ea) => {};

            if (this.faultTolerantMethodReceivingLink != null)
            {
                receivingLinkCloseTask = this.faultTolerantMethodReceivingLink.CloseAsync();
                this.faultTolerantMethodReceivingLink = null;
            }
            else
            {
                receivingLinkCloseTask = TaskHelpers.CompletedTask;
            }

            Task sendingLinkCloseTask;
            if (this.faultTolerantMethodSendingLink != null)
            {
                sendingLinkCloseTask = this.faultTolerantMethodSendingLink.CloseAsync();
                this.faultTolerantMethodSendingLink = null;
            }
            else
            {
                sendingLinkCloseTask = TaskHelpers.CompletedTask;
            }

            await Task.WhenAll(receivingLinkCloseTask, sendingLinkCloseTask);
#else
            throw new NotImplementedException();
#endif
        }

        public override async Task SendMethodResponseAsync(MethodResponseInternal methodResponse, CancellationToken cancellationToken)
        {
            await this.HandleTimeoutCancellation(async () =>
            {
                Outcome outcome;
                using (AmqpMessage amqpMessage = methodResponse.ToAmqpMessage())
                {
                    outcome = await this.SendAmqpMethodResponseAsync(amqpMessage, cancellationToken);
                }

                if (outcome.DescriptorCode != Accepted.Code)
                {
                    throw AmqpErrorMapper.GetExceptionFromOutcome(outcome);
                }
            }, cancellationToken);
        }

        public override Task CompleteAsync(string lockToken, CancellationToken cancellationToken)
        {
            return this.HandleTimeoutCancellation(() => this.DisposeMessageAsync(lockToken, AmqpConstants.AcceptedOutcome, cancellationToken), cancellationToken);
        }

        public override Task AbandonAsync(string lockToken, CancellationToken cancellationToken)
        {
            return this.HandleTimeoutCancellation(() => this.DisposeMessageAsync(lockToken, AmqpConstants.ReleasedOutcome, cancellationToken), cancellationToken);
        }

        public override Task RejectAsync(string lockToken, CancellationToken cancellationToken)
        {
            return this.HandleTimeoutCancellation(() => this.DisposeMessageAsync(lockToken, AmqpConstants.RejectedOutcome, cancellationToken), cancellationToken);
        }

        protected override async void Dispose(bool disposing)
        {
            try
            {
                await this.CloseAsync();
            }
            catch
            {
                // TODO: add traces here
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        public override async Task CloseAsync()
        {
            if (Interlocked.CompareExchange(ref this.closed, 1, 0) == 0)
            {
                GC.SuppressFinalize(this);
                Task eventSendingLinkCloseTask = this.faultTolerantEventSendingLink.CloseAsync();
                Task deviceBoundReceivingLinkCloseTask = this.faultTolerantDeviceBoundReceivingLink.CloseAsync();
#if WIP_C2D_METHODS_AMQP
                Task disabledMethodTask = this.DisableMethodsAsync(CancellationToken.None);
                await Task.WhenAll(eventSendingLinkCloseTask, deviceBoundReceivingLinkCloseTask, disabledMethodTask);
#else
                await Task.WhenAll(eventSendingLinkCloseTask, deviceBoundReceivingLinkCloseTask);
#endif
                this.IotHubConnection.Release(this.deviceId);
            }
        }

        async Task<Outcome> SendAmqpMessageAsync(AmqpMessage amqpMessage, CancellationToken cancellationToken)
        {
            Outcome outcome;
            try
            {
                SendingAmqpLink eventSendingLink = await this.GetEventSendingLinkAsync(cancellationToken);
                outcome = await eventSendingLink.SendMessageAsync(amqpMessage, new ArraySegment<byte>(Guid.NewGuid().ToByteArray()), AmqpConstants.NullBinary, this.operationTimeout);
            }
            catch (Exception exception)
            {
                if (exception.IsFatal())
                {
                    throw;
                }

                throw AmqpClientHelper.ToIotHubClientContract(exception);
            }

            return outcome;
        }

        async Task<Outcome> SendAmqpMethodResponseAsync(AmqpMessage amqpMessage, CancellationToken cancellationToken)
        {
            Outcome outcome;
            try
            {
                SendingAmqpLink methodRespSendingLink = await this.GetMethodSendingLinkAsync(cancellationToken);
                outcome = await methodRespSendingLink.SendMessageAsync(amqpMessage, new ArraySegment<byte>(Guid.NewGuid().ToByteArray()), AmqpConstants.NullBinary, this.operationTimeout);
            }
            catch (Exception exception)
            {
                if (exception.IsFatal())
                {
                    throw;
                }

                throw AmqpClientHelper.ToIotHubClientContract(exception);
            }

            return outcome;
        }
        public override async Task<Twin> SendTwinGetAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException("Device twins are only supported with Mqtt protocol.");
        }

        async Task DisposeMessageAsync(string lockToken, Outcome outcome, CancellationToken cancellationToken)
        {
            ArraySegment<byte> deliveryTag = IotHubConnection.ConvertToDeliveryTag(lockToken);

            Outcome disposeOutcome;
            try
            {
                ReceivingAmqpLink deviceBoundReceivingLink = await this.GetDeviceBoundReceivingLinkAsync(cancellationToken);
                disposeOutcome = await deviceBoundReceivingLink.DisposeMessageAsync(deliveryTag, outcome, batchable: true, timeout: this.operationTimeout);
            }
            catch (Exception exception)
            {
                if (exception.IsFatal())
                {
                    throw;
                }

                throw AmqpClientHelper.ToIotHubClientContract(exception);
            }

            if (disposeOutcome.DescriptorCode != Accepted.Code)
            {
                if (disposeOutcome.DescriptorCode == Rejected.Code)
                {
                    var rejected = (Rejected)disposeOutcome;

                    // Special treatment for NotFound amqp rejected error code in case of DisposeMessage 
                    if (rejected.Error != null && rejected.Error.Condition.Equals(AmqpErrorCode.NotFound))
                    {
                        throw new DeviceMessageLockLostException(rejected.Error.Description);
                    }
                }

                throw AmqpErrorMapper.GetExceptionFromOutcome(disposeOutcome);
            }
        }

        static AmqpLinkSettings SetLinkSettingsCommonProperties(AmqpLinkSettings linkSettings, TimeSpan timeSpan)
        {
            linkSettings.AddProperty(IotHubAmqpProperty.TimeoutName, timeSpan.TotalMilliseconds);
#if WINDOWS_UWP
            // System.Reflection.Assembly.GetExecutingAssembly() does not exist for UWP, therefore use a hard-coded version name
            // (This string is picked up by the bump_version script, so don't change the line below)
            var UWPAssemblyVersion = "1.2.12";
            linkSettings.AddProperty(IotHubAmqpProperty.ClientVersion, UWPAssemblyVersion);
#elif PCL
            string PCLAssemblyVersion = "Microsoft.Azure.Devices.Client/1.2.12";
            linkSettings.AddProperty(IotHubAmqpProperty.ClientVersion, PCLAssemblyVersion);
#else
            linkSettings.AddProperty(IotHubAmqpProperty.ClientVersion, Utils.GetClientVersion());
#endif
            return linkSettings;
        }

        static AmqpLinkSettings SetLinkSettingsCommonPropertiesForMethod(AmqpLinkSettings linkSettings, string deviceId)
        {
            linkSettings.AddProperty(IotHubAmqpProperty.ApiVersion, ClientApiVersionHelper.ApiVersionString);
            linkSettings.AddProperty(IotHubAmqpProperty.ChannelCorrelationId, deviceId);
            return linkSettings;
        }
        async Task<SendingAmqpLink> GetEventSendingLinkAsync(CancellationToken cancellationToken)
        {
            SendingAmqpLink eventSendingLink;
            if (!this.faultTolerantEventSendingLink.TryGetOpenedObject(out eventSendingLink))
            {
                eventSendingLink = await this.faultTolerantEventSendingLink.GetOrCreateAsync(this.openTimeout, cancellationToken);
            }
            return eventSendingLink;
        }

        async Task<SendingAmqpLink> CreateEventSendingLinkAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            string path = string.Format(CultureInfo.InvariantCulture, CommonConstants.DeviceEventPathTemplate, System.Net.WebUtility.UrlEncode(this.deviceId));

            var linkAddress = this.IotHubConnection.BuildLinkAddress(this.iotHubConnectionString, path);

            var linkSettings = new AmqpLinkSettings()
            {
                Role = false,
                InitialDeliveryCount = 0,
                Target = new Target() {Address = linkAddress.AbsoluteUri},
                SndSettleMode = null, // SenderSettleMode.Unsettled (null as it is the default and to avoid bytes on the wire)
                RcvSettleMode = null, // (byte)ReceiverSettleMode.First (null as it is the default and to avoid bytes on the wire)
                LinkName = Guid.NewGuid().ToString("N") // Use a human readable link name to help with debugging
            };

            SetLinkSettingsCommonProperties(linkSettings, timeout);

            return await this.IotHubConnection.CreateSendingLinkAsync(path, this.iotHubConnectionString, linkSettings, timeout, cancellationToken);
        }

        async Task<ReceivingAmqpLink> GetDeviceBoundReceivingLinkAsync(CancellationToken cancellationToken)
        {
            ReceivingAmqpLink deviceBoundReceivingLink;
            if (!this.faultTolerantDeviceBoundReceivingLink.TryGetOpenedObject(out deviceBoundReceivingLink))
            {
                deviceBoundReceivingLink = await this.faultTolerantDeviceBoundReceivingLink.GetOrCreateAsync(this.openTimeout, cancellationToken);
            }

            return deviceBoundReceivingLink;
        }

        async Task<ReceivingAmqpLink> CreateDeviceBoundReceivingLinkAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            string path = string.Format(CultureInfo.InvariantCulture, CommonConstants.DeviceBoundPathTemplate, System.Net.WebUtility.UrlEncode(this.deviceId));

            var linkAddress = this.IotHubConnection.BuildLinkAddress(this.iotHubConnectionString, path);

            var linkSettings = new AmqpLinkSettings()
            {
                Role = true,
                TotalLinkCredit = prefetchCount,
                AutoSendFlow = prefetchCount > 0,
                Source = new Source() {Address = linkAddress.AbsoluteUri},
                SndSettleMode = null, // SenderSettleMode.Unsettled (null as it is the default and to avoid bytes on the wire)
                RcvSettleMode = (byte)ReceiverSettleMode.Second,
                LinkName = Guid.NewGuid().ToString("N") // Use a human readable link name to help with debuggin
            };

            var timeoutHelper = new TimeoutHelper(timeout);

            SetLinkSettingsCommonProperties(linkSettings, timeoutHelper.RemainingTime());

            return await this.IotHubConnection.CreateReceivingLinkAsync(path, this.iotHubConnectionString, linkSettings, timeout, this.prefetchCount, cancellationToken);
        }

        async Task<SendingAmqpLink> GetMethodSendingLinkAsync(CancellationToken cancellationToken)
        {
            SendingAmqpLink methodSendingLink;
            if (!this.faultTolerantMethodSendingLink.TryGetOpenedObject(out methodSendingLink))
            {
                methodSendingLink = await this.faultTolerantMethodSendingLink.GetOrCreateAsync(this.openTimeout, cancellationToken);
            }
            return methodSendingLink;
        }

        async Task<SendingAmqpLink> CreateMethodSendingLinkAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            string path = string.Format(CultureInfo.InvariantCulture, CommonConstants.DeviceMethodPathTemplate, System.Net.WebUtility.UrlEncode(this.deviceId));

            var linkAddress = this.IotHubConnection.BuildLinkAddress(this.iotHubConnectionString, path);

            var linkSettings = new AmqpLinkSettings()
            {
                Role = false,
                InitialDeliveryCount = 0,
                Target = new Target() {Address = linkAddress.AbsoluteUri},
                SndSettleMode = (byte)SenderSettleMode.Settled,
                RcvSettleMode = (byte)ReceiverSettleMode.First,
                LinkName = Guid.NewGuid().ToString("N") // Use a human readable link name to help with debugging
            };

            SetLinkSettingsCommonProperties(linkSettings, timeout);
            SetLinkSettingsCommonPropertiesForMethod(linkSettings, this.deviceId);

            return await this.IotHubConnection.CreateSendingLinkAsync(path, this.iotHubConnectionString, linkSettings, timeout, cancellationToken);
        }

        async Task<ReceivingAmqpLink> GetMethodReceivingLinkAsync(CancellationToken cancellationToken)
        {
            ReceivingAmqpLink methodReceivingLink;
            if (!this.faultTolerantMethodReceivingLink.TryGetOpenedObject(out methodReceivingLink))
            {
                methodReceivingLink = await this.faultTolerantMethodReceivingLink.GetOrCreateAsync(this.openTimeout, cancellationToken);
            }

            return methodReceivingLink;
        }

        async Task<ReceivingAmqpLink> CreateMethodReceivingLinkAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            string path = string.Format(CultureInfo.InvariantCulture, CommonConstants.DeviceMethodPathTemplate, System.Net.WebUtility.UrlEncode(this.deviceId));

            var linkAddress = this.IotHubConnection.BuildLinkAddress(this.iotHubConnectionString, path);

            var linkSettings = new AmqpLinkSettings()
            {
                Role = true,
                TotalLinkCredit = prefetchCount,
                AutoSendFlow = prefetchCount > 0,
                Source = new Source() { Address = linkAddress.AbsoluteUri },
                SndSettleMode = (byte)SenderSettleMode.Settled,
                RcvSettleMode = (byte)ReceiverSettleMode.First,
                LinkName = Guid.NewGuid().ToString("N") // Use a human readable link name to help with debuggin
            };

            SetLinkSettingsCommonProperties(linkSettings, timeout);
            SetLinkSettingsCommonPropertiesForMethod(linkSettings, deviceId);

            var link = await this.IotHubConnection.CreateReceivingLinkAsync(
                path, this.iotHubConnectionString, linkSettings, timeout, this.prefetchCount, cancellationToken);

            link.RegisterMessageListener(amqpMessage => 
                {
                    MethodRequestInternal methodRequestInternal = MethodConverter.ConstructMethodRequestFromAmqpMessage(amqpMessage);
                    link.DisposeDelivery(amqpMessage, true, AmqpConstants.AcceptedOutcome);
                    this.messageListener(methodRequestInternal);
                });

            return link;
        }
    }
}
