﻿// Copyright © 2012-2018 Vaughn Vernon. All rights reserved.
//
// This Source Code Form is subject to the terms of the
// Mozilla Public License, v. 2.0. If a copy of the MPL
// was not distributed with this file, You can obtain
// one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Vlingo.Actors;
using Vlingo.Wire.Channel;
using Vlingo.Wire.Message;

namespace Vlingo.Wire.Multicast
{
    public class MulticastPublisherReader : ChannelMessageDispatcher, IChannelPublisher, IDisposable
    {
        private readonly RawMessage _availability;
        private readonly Socket _publisherChannel;
        private bool _closed;
        private readonly IChannelReaderConsumer _consumer;
        private readonly EndPoint _groupAddress;
        private readonly ILogger _logger;
        private readonly Queue<RawMessage> _messageQueue;
        private readonly string _name;
        private readonly int _maxMessageSize;
        private readonly IPEndPoint _publisherAddress;
        private readonly Socket _readChannel;
        private readonly List<Socket> _clientReadChannels;
        private bool _disposed;
        private readonly ManualResetEvent _acceptDone;

        public MulticastPublisherReader(
            string name,
            Group group,
            int incomingSocketPort,
            int maxMessageSize,
            IChannelReaderConsumer consumer,
            ILogger logger)
        {
            _name = name;
            _maxMessageSize = maxMessageSize;
            _consumer = consumer;
            _logger = logger;
            _acceptDone = new ManualResetEvent(false);
            _groupAddress = new IPEndPoint(IPAddress.Parse(group.Address), group.Port);
            _messageQueue = new Queue<RawMessage>();
            
            _publisherChannel = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _publisherChannel.Blocking = false;
            _publisherChannel.ExclusiveAddressUse = false;
            // binds to an assigned local address that is
            // published as my availabilityMessage
            _publisherChannel.Bind(new IPEndPoint(IPAddress.Any, 0));
            
            _readChannel = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _readChannel.Blocking = false;
            _readChannel.ExclusiveAddressUse = false;
            _readChannel.Bind(new IPEndPoint(IPAddress.Any, incomingSocketPort));
            _readChannel.Listen(120);
            
            _publisherAddress = (IPEndPoint)_readChannel.LocalEndPoint;
            
            _clientReadChannels = new List<Socket>();
            
            _availability = AvailabilityMessage();
        }
        
        //====================================
        // ChannelPublisher
        //====================================

        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            try
            {
                _messageQueue.Clear(); // messages are lost anyway
                _publisherChannel.Close();
            }
            catch (Exception e)
            {
                _logger.Error($"Failed to close multicast publisher selector for: '{_name}'", e);
            }
            
            try
            {
                _readChannel.Close();
                foreach (var clientReadChannel in _clientReadChannels.ToArray())
                {
                    clientReadChannel.Close();
                }
            }
            catch (Exception e)
            {
                _logger.Error($"Failed to close multicast reader channel for: '{_name}'", e);
            }
        }

        public void ProcessChannel()
        {
            if (_closed)
            {
                return;
            }

            try
            {
                ProbeChannel();
                SendMax();
            }
            catch (SocketException e)
            {
                _logger.Error($"Failed to read channel selector for: '{_name}'", e);
            }
        }

        public void SendAvailability() => Send(_availability);

        public void Send(RawMessage message)
        {
            if (_closed)
            {
                return;
            }
            
            var length = message.Length;

            if (length <= 0)
            {
                throw new ArgumentException("The message length must be greater than zero.");
            }

            if (length > _maxMessageSize)
            {
                throw new ArgumentException($"The message length is greater than {_maxMessageSize}");
            }
            
            _messageQueue.Enqueue(message);
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);  
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }
      
            if (disposing) 
            {
                Close();
            }
      
            _disposed = true;
        }
        
        //====================================
        // ChannelMessageDispatcher
        //====================================

        public override IChannelReaderConsumer Consumer => _consumer;

        public override ILogger Logger => _logger;

        public override string Name => _name;
        
        //====================================
        // internal implementation
        //====================================
        
        public void ProbeChannel()
        {
            try
            {
                Accept();

                foreach (var clientReadChannel in _clientReadChannels.ToArray())
                {
                    if (clientReadChannel.Available > 0)
                    {
                        new SocketChannelSelectionReader(this).Read(clientReadChannel, new RawMessageBuilder(_maxMessageSize));
                    }
                    
                    if (!clientReadChannel.IsSocketConnected())
                    {
                        clientReadChannel.Close();
                        _clientReadChannels.Remove(clientReadChannel);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to read channel selector for: '{_name}' because: {e.Message}", e);
            }
        }
        
        private void Accept()
        {
            try
            {
                if (_readChannel.Poll(10000, SelectMode.SelectRead))
                {
                    _readChannel.BeginAccept(AcceptCallback, _readChannel);
                    _acceptDone.WaitOne();
                }
            }
            catch (Exception e)
            {
                var message = $"Failed to accept client socket for {_name} because: {e.Message}";
                Logger.Error(message, e);
                throw;
            }
        }
        
        private RawMessage AvailabilityMessage()
        {
            var message = new PublisherAvailability(
                _name,
                GetHostName(_publisherAddress.Address),
                _publisherAddress.Port).ToString();
            
            var buffer = new MemoryStream(message.Length);
            var messageBytes = Converters.TextToBytes(message);
            buffer.Write(messageBytes, 0, messageBytes.Length); // TODO: Can be done async
            buffer.Flip();

            return RawMessage.ReadFromWithoutHeader(buffer);
        }

        private string GetHostName(IPAddress publisherAddress)
        {
            try
            {
                return Dns.GetHostEntry(publisherAddress).HostName;
            }
            catch
            {
                if (publisherAddress.ToString().StartsWith("::"))
                {
                    return "localhost";
                }
                
                return publisherAddress.ToString();
            }
        }

        private void SendMax()
        {
            while (true)
            {
                if (_messageQueue.Count == 0)
                {
                    return;
                }
                
                var message = _messageQueue.Peek();

                var buffer = message.AsBuffer(new MemoryStream(_maxMessageSize));
                _publisherChannel.BeginSendTo(buffer, 0, buffer.Length, SocketFlags.None, _groupAddress, SendToCallback, _publisherChannel);
            }
        }
        
        private void AcceptCallback(IAsyncResult ar)
        {
            // Get the socket that handles the client request.  
            var listener = (Socket)ar.AsyncState;
            var clientChannel = listener.EndAccept(ar);  
            _clientReadChannels.Add(clientChannel);
            _acceptDone.Set();
        }

        private void SendToCallback(IAsyncResult ar)
        {
            var publisherChannel = (Socket)ar.AsyncState;

            var sent = publisherChannel.EndSendTo(ar);
            
            if (sent > 0)
            {
                _messageQueue.Dequeue();
            }
        }
    }
}
