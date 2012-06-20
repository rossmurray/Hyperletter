using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using Hyperletter.Abstraction;
using Hyperletter.Core.Channel;
using Hyperletter.Core.Extension;

namespace Hyperletter.Core {
    public abstract class AbstractChannel : IAbstractChannel {
        private readonly Guid _hyperSocketId;
        private readonly SocketMode _socketMode;
        private const int HeartbeatInterval = 1000;

        protected TcpClient TcpClient;

        private CancellationTokenSource _cancellationTokenSource;
        private LetterTransmitter _transmitter;
        private LetterReceiver _receiver;

        private ILetter _sending;

        private readonly ManualResetEventSlim _cleanUpLock = new ManualResetEventSlim(true);
        
        private readonly Timer _heartbeat;
        private int _lastAction;
        private int _lastActionHeartbeat;

        public event Action<IAbstractChannel> ChannelConnected;
        public event Action<IAbstractChannel> ChannelDisconnected;

        public event Action<IAbstractChannel, ILetter> Received;
        public event Action<IAbstractChannel, ILetter> Sent;
        public event Action<IAbstractChannel, ILetter> FailedToSend;

        private IPart _talkingTo;
        
        public bool IsConnected { get; private set; }
        public Binding Binding { get; private set; }

        protected AbstractChannel(Guid hyperSocketId, SocketMode socketMode, Binding binding) {
            _hyperSocketId = hyperSocketId;
            _socketMode = socketMode;
            Binding = binding;

            _heartbeat = new Timer(Heartbeat);
        }

        public virtual void Initialize() {
        }

        protected void Heartbeat(object state) {
            if (_lastAction != _lastActionHeartbeat) {
                _lastActionHeartbeat = _lastAction;
                _transmitter.TransmitHeartbeat();
            }
        }

        protected void Connected() {
            IsConnected = true;

            _cancellationTokenSource = new CancellationTokenSource();

            _transmitter = new LetterTransmitter(TcpClient.Client, _cancellationTokenSource);
            _transmitter.Sent += TransmitterOnSent;
            _transmitter.SocketError += SocketError;
            _transmitter.Start();

            _receiver = new LetterReceiver(TcpClient.Client, _cancellationTokenSource);
            _receiver.Received += ReceiverReceived;
            _receiver.SocketError += SocketError;
            _receiver.Start();

            _heartbeat.Change(HeartbeatInterval, HeartbeatInterval);

            var letter = new Letter { Type = LetterType.Initialize, Parts = new IPart[] { new Part { Data = _hyperSocketId.ToByteArray() } } };
            Enqueue(letter);

            ChannelConnected(this);
        }

        protected void Disconnected() {
            if (!IsConnected)
                return;
            IsConnected = false;
            _heartbeat.Change(Timeout.Infinite, Timeout.Infinite);

            _cancellationTokenSource.Cancel();
            TcpClient.Client.Disconnect(false);

            ChannelDisconnected(this);
            
            AfterDisconnected();
        }

        protected virtual void AfterDisconnected() { }

        public void Enqueue(ILetter letter) {
            _cleanUpLock.Wait();
            _sending = letter;
            _transmitter.Enqueue(new TransmitContext(letter));
        }

        private void ReceiverReceived(ILetter receivedLetter) {
            ResetHeartbeatTimer();

            if (receivedLetter.Type == LetterType.Ack) {
                HandleLetterSent();
            } else {
                if (receivedLetter.Type == LetterType.Initialize)
                    _talkingTo = receivedLetter.Parts[0];

                if (receivedLetter.Options.IsSet(LetterOptions.NoAck))
                    Received(this, receivedLetter);
                else
                    QueueAck(receivedLetter);
            }
        }

        private void TransmitterOnSent(TransmitContext transmitContext) {
            ResetHeartbeatTimer();

            if(transmitContext.Letter.Type == LetterType.Ack)
                HandleAckSent(transmitContext);
            else if (transmitContext.Letter.Options.IsSet(LetterOptions.NoAck))
                HandleLetterSent();
        }

        private void HandleLetterSent() {
            Sent(this, _sending);
        }

        private void HandleAckSent(TransmitContext deliveryContext) {
            var receivedLetter = deliveryContext.Context;

            if(receivedLetter.Type == LetterType.User) {
                Received(this, receivedLetter);
            } else if(receivedLetter.Type == LetterType.Initialize)
                _talkingTo = receivedLetter.Parts[0];
        }
      
        private void QueueAck(ILetter letter) {
            var ack = new Letter {Type = LetterType.Ack, Id = letter.Id };
            _transmitter.Enqueue(new TransmitContext(ack, letter));
        }

        private void SocketError() {
            _cleanUpLock.Reset();
            
            _cancellationTokenSource.Cancel();
            FailQueuedLetters();
            Disconnected();

            _cleanUpLock.Set();
        }

        private void FailQueuedLetters() {
            if (_sending != null)
                FailedToSend(this, _sending);
        }

        private void ResetHeartbeatTimer() {
            _lastAction++;
            if (_lastAction > 10000000)
                _lastAction = 0;
        }
    }
}