﻿using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;

namespace SaorCon
{
    public enum BoseCommand
    {
        ConnectCommand,
        QueryStatusCommand,
        QueryBatteryCommand,
        SetAncCommand
    }

    public class BoseUnsubscriber : IDisposable
    {
        private List<IObserver<BoseMessage>> _observers;
        private IObserver<BoseMessage> _observer;

        public BoseUnsubscriber( List<IObserver<BoseMessage>> observers, IObserver<BoseMessage> observer )
        {
            this._observers = observers;
            this._observer = observer;
        }

        public void Dispose()
        {
            if ( !( _observer == null ) ) _observers.Remove( _observer );
        }
    }


    public class BoseDeviceDefault : IBoseDevice
    {

        public bool     Connected       { get => bluetoothClient != null && bluetoothClient.Connected; }
        public bool     SoftConnect     { get; private set; } = false;
        public int LastLowBatteryNotificationPercentage { get; set; } = 100;
        public Int16    AncLevel        { get; private set; } = -1;
        public Int16    BatteryLevel    { get; private set; } = -1;
        public string   DeviceName      { get; private set; } = "Unknown Device";
        public string   DeviceId        { get; } = null;

        public BoseDeviceDefault ( BluetoothDevice device )
        {
            bluetoothDevice = device;
            DeviceName = device.Name;
            DeviceId = device.DeviceId;

            if ( bluetoothDevice.ConnectionStatus == BluetoothConnectionStatus.Connected )
                Connect();

            bluetoothDevice.ConnectionStatusChanged += DeviceConnectionStateChanged;
        }

        ~BoseDeviceDefault()
        {
            Disconnect();
            bluetoothDevice = null;
        }

        private void Disconnect()
        {
            m_readTaskCancellation.Cancel();
            m_requestTaskToken?.Cancel();

            m_requestMutex.WaitOne();
            m_requestMutex.Dispose();

            if ( bluetoothStream != null )
            {
                bluetoothStream.Dispose();
                bluetoothStream.Close();
                bluetoothStream = null;
            }

            if ( bluetoothClient != null && bluetoothClient.Connected )
                bluetoothClient.Dispose();

            bluetoothClient = null;
            SoftConnect = false;
            LastLowBatteryNotificationPercentage = 100;

            foreach ( var observer in m_observers )
                observer.OnNext( BoseMessage.DisconnectMessage );
        }

        public void SetAncLevel( Int16 level )
        {
            if ( !Connected )
                return;

            if ( level == 1 )
                level = 3;
            else if ( level == 2 )
                level = 1;
            else if ( level != 0 )
                throw new ArgumentOutOfRangeException( "Invalid ANC level" );

            if ( m_ancLevelSet == level )
                return;

            m_ancLevelSet = level;
            SendCommand( BoseCommand.SetAncCommand, new byte[] { Convert.ToByte( level ) } );
            
            // TODO - set task to trigger after ~1 second to call this function again if level ACK not received
        }

        private async Task<IAsyncResult> DelayedRequest( CancellationToken token )
        {
            await Task.Delay( 100 );
            if ( token.IsCancellationRequested )
            {
                return Task.CompletedTask;
            }

            m_requestMutex.WaitOne();
            var result = SendCommand( m_requestBuffer.Item1, m_requestBuffer.Item2, true );
            m_requestMutex.ReleaseMutex();

            return Task.FromResult( result );
        }

        private IAsyncResult SendCommand( BoseCommand command, byte[] payload = null, bool force = false )
        {
            // TODO - these should be sent to a fixed-size buffer to avoid a barrage of requests
            if ( !bluetoothClient.Connected )
                throw new Exception();

            var currentTime = Convert.ToUInt64( new TimeSpan( DateTime.Now.Ticks ).TotalMilliseconds );
            if( !force && currentTime < m_requestCooldown )
            {
                m_requestCooldown = currentTime + 100;

                m_requestTaskToken.Cancel();
                m_requestTaskToken = new CancellationTokenSource();

                m_requestMutex.WaitOne();
                m_requestBuffer = new Tuple<BoseCommand, byte[]>(command, payload);
                m_requestMutex.ReleaseMutex();

                CancellationToken token = m_requestTaskToken.Token;
                return Task.Run( async () => { await DelayedRequest(token); }, m_requestTaskToken.Token );
            }

            m_requestCooldown = currentTime + 100;

            var baseCode = BoseCommandCodes[command];
            byte payloadLength = ( payload != null ) ? Convert.ToByte( payload.Length ) : (byte)0;

            var finalCommandLength = baseCode.Length + payloadLength + 1;

            byte[] finalCommand = new byte[finalCommandLength];

            Array.Copy( baseCode, finalCommand, baseCode.Length );
            finalCommand[baseCode.Length] = Convert.ToByte( payloadLength );
            if ( payloadLength > 0 )
                Array.Copy( payload, 0, finalCommand, baseCode.Length + 1, payloadLength );

            return bluetoothStream.BeginWrite( finalCommand, 0, finalCommand.Length, OnCommandSent, this );
        }

        private void OnCommandSent( IAsyncResult result )
        {
            try
            {
                bluetoothStream.EndWrite( result );
            }
            catch( Exception )
            {
                // TODO
            }
        }

        private BoseMessage GetMessageFromHeader( byte[] header )
        {
            if( header.Length < 3 )
            {
                // TODO
                return BoseMessage.Unknown;
            }

            foreach( var messagePair in BoseMessageCodes )
            {
                bool found = true;
                for ( int i = 0; i < 3; ++i )
                    if ( !( found = ( header[i] == messagePair.Value[i] ) ) )
                        break;
                if ( found )
                    return messagePair.Key;
            }

            return BoseMessage.Unknown;
        }

        private byte[] GetPayloadFromHeader( byte[] header )
        {
            if ( header.Length < 4 )
            {
                // TODO
                return null;
            }
            var payloadLength = header[3];
            
            if ( payloadLength == 0 )
                return null;

            var payload = new byte[payloadLength];
            bluetoothStream.Read( payload, 0, payload.Length );
            // TODO - don't hang
            return payload;

        }

        private Int16 ConvertBatteryLevel( byte[] payload )
        {
            if ( payload.Length != 1 )
                //TODO
                return -1;

            BatteryUpdated(this, Convert.ToInt16(payload[0]));

            return Convert.ToInt16( payload[0] );
        }

        private static Int16 ConvertAncLevel( byte[] payload )
        {
            if ( payload.Length != 2 )
                //TODO
                return -1;

            Int16 level = Convert.ToInt16( payload[0] );

            if ( level == 1 ) level = 2;
            else if ( level == 3 ) level = 1;
            else if ( level != 0 ) throw new ArgumentException( $"Received invalid ANC level: {level}" );

            return level;
        }

        private void ReadIncomingMessages()
        {
            if ( !Connected )
                return;

            while ( bluetoothStream.DataAvailable )
            {
                byte[] header = new byte[4];
                bluetoothStream.Read( header, 0, header.Length );

                // TODO - combine these
                BoseMessage message = GetMessageFromHeader( header );
                var payload = GetPayloadFromHeader( header );

                if ( message == BoseMessage.Unknown )
                    continue;

                m_messageHandlers[message]( this, payload );

                foreach ( var observer in m_observers )
                    observer.OnNext( message );
            }
        }

        public IAsyncResult Connect()
        {
            if ( bluetoothClient != null && bluetoothClient.Connected )
                return null;

            bluetoothClient = new BluetoothClient();

            var address = new BluetoothAddress( unchecked((long)bluetoothDevice.BluetoothAddress) ); // Bitwise conversion
            var bluetoothEndPoint = new BluetoothEndPoint( address, BluetoothService.SerialPort, BoseRFCommChannel );

            return bluetoothClient.BeginConnect( bluetoothEndPoint, new AsyncCallback( OnConnected ), null );
        }

        private void OnConnected( IAsyncResult result )
        {
            try
            {
                bluetoothClient.EndConnect( result );
                if ( !Connected )
                    throw new Exception();
            }
            catch ( Exception )
            {
                Console.Write( "Failed to connect to device" );
                bluetoothClient = null;
                return;
            }

            bluetoothStream = bluetoothClient.GetStream();

            SendCommand( BoseCommand.ConnectCommand, force: true );
            SendCommand( BoseCommand.QueryStatusCommand, force: true );
            SendCommand( BoseCommand.QueryBatteryCommand, force: true );

            m_readTaskCancellation = new CancellationTokenSource();
            var token = m_readTaskCancellation.Token;
            Task.Factory.StartNew( () =>
                {
                    while ( Connected && !token.IsCancellationRequested )
                    {
                        ReadIncomingMessages();
                        Thread.Sleep( 1000 );
                    }
                }, token );            
        }

        private void DeviceConnectionStateChanged( BluetoothDevice sender, object args )
        {
            var newState = sender.ConnectionStatus;

            if ( newState == BluetoothConnectionStatus.Connected )
            {
                if ( bluetoothClient == null )
                    Connect();
            }
            else
                Disconnect();
        }

        public IDisposable Subscribe( IObserver<BoseMessage> observer )
        {
            if ( !m_observers.Contains( observer ) )
                m_observers.Add( observer );

            return new BoseUnsubscriber( m_observers, observer );
        }

        private Int16 m_ancLevelSet = -1;
        private BluetoothDevice bluetoothDevice;
        private BluetoothClient bluetoothClient;
        private NetworkStream bluetoothStream;
        private CancellationTokenSource m_readTaskCancellation = new CancellationTokenSource();

        private List<IObserver<BoseMessage>> m_observers = new List<IObserver<BoseMessage>>();

        private static readonly int BoseRFCommChannel = 8;

        private static readonly Dictionary<BoseCommand, byte[]> BoseCommandCodes = new Dictionary<BoseCommand, byte[]>
        {
            { BoseCommand.ConnectCommand,      new byte[] { 0x00, 0x01, 0x01 } },
            { BoseCommand.QueryStatusCommand,  new byte[] { 0x01, 0x01, 0x05 } },
            { BoseCommand.QueryBatteryCommand, new byte[] { 0x02, 0x02, 0x01 } },
            { BoseCommand.SetAncCommand,       new byte[] { 0x01, 0x06, 0x02 } }
        };

        private static readonly Dictionary<BoseMessage, byte[]> BoseMessageCodes = new Dictionary<BoseMessage, byte[]>
        {
            { BoseMessage.ConnectAckMessage,    new byte[] { 0x00, 0x01, 0x03 } },
            { BoseMessage.AncLevelMessage,      new byte[] { 0x01, 0x06, 0x03 } },
            { BoseMessage.BatteryLevelMessage,  new byte[] { 0x02, 0x02, 0x03 } }
        };

        delegate void MessageHandler( BoseDeviceDefault sender, byte[] payload = null );

        public delegate void BatteryLevelMessageHandler(BoseDeviceDefault sender, int level);
        
        public event BatteryLevelMessageHandler BatteryUpdated;

        private Dictionary<BoseMessage, MessageHandler> m_messageHandlers = new Dictionary<BoseMessage, MessageHandler>()
        {
            { BoseMessage.ConnectAckMessage, new MessageHandler( (sender, p) => sender.SoftConnect = true ) },
            { BoseMessage.AncLevelMessage, new MessageHandler( (sender, p) => sender.AncLevel = ConvertAncLevel(p) ) },
            { BoseMessage.BatteryLevelMessage, new MessageHandler( (sender, p) => sender.BatteryLevel = sender.ConvertBatteryLevel(p) ) }
        };

        private ulong m_requestCooldown = 0;
        private Tuple<BoseCommand, byte[]> m_requestBuffer;
        private CancellationTokenSource m_requestTaskToken = new CancellationTokenSource();
        private Mutex m_requestMutex = new Mutex();
    }
}
