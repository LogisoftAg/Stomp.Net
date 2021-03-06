#region Usings

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Extend;
using Stomp.Net.Stomp.Commands;
using Stomp.Net.Stomp.Transport;

#endregion

namespace Stomp.Net.Stomp.Protocol
{
    /// <summary>
    ///     Implements the <a href="http://stomp.codehaus.org/">STOMP</a> protocol.
    /// </summary>
    public class StompWireFormat : IWireFormat
    {
        #region Fields

        private Int32 _connectedResponseId = -1;
        private Boolean _encodeHeaders;
        private WireFormatInfo _remoteWireFormatInfo;

        #endregion

        #region Properties

        public Encoding Encoding { get; } = Encoding.UTF8;

        public Int32 MaxInactivityDuration { get; } = 30000;

        public Int32 MaxInactivityDurationInitialDelay { get; } = 0;

        public Int32 ReadCheckInterval => MaxInactivityDuration;

        public Int32 WriteCheckInterval => MaxInactivityDuration > 3 ? MaxInactivityDuration / 3 : MaxInactivityDuration;

        public ITransport Transport { get; set; }

        #endregion

        public void Marshal( Object o, BinaryWriter writer )
        {
            switch ( o )
            {
                case ConnectionInfo info:
                    WriteConnectionInfo( info, writer );
                    break;
                case Message _:
                    WriteMessage( (Message) o, writer );
                    break;
                case ConsumerInfo _:
                    WriteConsumerInfo( (ConsumerInfo) o, writer );
                    break;
                case MessageAck _:
                    WriteMessageAck( (MessageAck) o, writer );
                    break;
                case TransactionInfo _:
                    WriteTransactionInfo( (TransactionInfo) o, writer );
                    break;
                case ShutdownInfo _:
                    WriteShutdownInfo( (ShutdownInfo) o, writer );
                    break;
                case RemoveInfo _:
                    WriteRemoveInfo( (RemoveInfo) o, writer );
                    break;
                case KeepAliveInfo _:
                    WriteKeepAliveInfo( (KeepAliveInfo) o, writer );
                    break;
                case ICommand command:
                    if ( !command.ResponseRequired )
                        return;
                    var response = new Response { CorrelationId = command.CommandId };
                    SendCommand( response );
                    break;
                default:
                    Tracer.Warn( $"StompWireFormat - Ignored command: {o.GetType()} => '{0}'" );
                    break;
            }
        }

        public ICommand Unmarshal( BinaryReader reader )
        {
            var frame = new StompFrame( _encodeHeaders );
            frame.FromStream( reader );

            var answer = CreateCommand( frame );
            return answer;
        }

        protected virtual ICommand CreateCommand( StompFrame frame )
        {
            var command = frame.Command;

            switch ( command )
            {
                case "RECEIPT":
                {
                    var text = frame.RemoveProperty( "receipt-id" );
                    if ( text != null )
                    {
                        var answer = new Response();
                        if ( text.StartsWith( "ignore:", StringComparison.Ordinal ) )
                            text = text.Substring( "ignore:".Length );

                        answer.CorrelationId = Int32.Parse( text );
                        return answer;
                    }
                }
                    break;
                case "CONNECTED":
                    return ReadConnected( frame );
                case "ERROR":
                {
                    var text = frame.RemoveProperty( "receipt-id" );

                    if ( text != null && text.StartsWith( "ignore:", StringComparison.Ordinal ) )
                        return new Response { CorrelationId = Int32.Parse( text.Substring( "ignore:".Length ) ) };

                    var answer = new ExceptionResponse();
                    if ( text != null )
                        answer.CorrelationId = Int32.Parse( text );

                    var error = new BrokerError { Message = frame.RemoveProperty( "message" ) };
                    answer.Exception = error;
                    return answer;
                }
                case "KEEPALIVE":
                    return new KeepAliveInfo();
                case "MESSAGE":
                    return ReadMessage( frame );
            }

            Tracer.Error( "Unknown command: " + frame.Command + " headers: " + frame.Properties );

            return null;
        }

        protected virtual ICommand ReadConnected( StompFrame frame )
        {
            _remoteWireFormatInfo = new WireFormatInfo();

            if ( frame.HasProperty( "version" ) )
            {
                _remoteWireFormatInfo.Version = Single.Parse( frame.RemoveProperty( "version" ),
                                                              CultureInfo.InvariantCulture );
                if ( _remoteWireFormatInfo.Version > 1.0f )
                    _encodeHeaders = true;

                if ( frame.HasProperty( "session" ) )
                    _remoteWireFormatInfo.Session = frame.RemoveProperty( "session" );

                if ( frame.HasProperty( "heart-beat" ) )
                {
                    var hearBeats = frame.RemoveProperty( "heart-beat" )
                                         .Split( ",".ToCharArray() );
                    if ( hearBeats.Length != 2 )
                        throw new IoException( "Malformed heartbeat property in Connected Frame." );

                    _remoteWireFormatInfo.WriteCheckInterval = Int32.Parse( hearBeats[0]
                                                                                .Trim() );
                    _remoteWireFormatInfo.ReadCheckInterval = Int32.Parse( hearBeats[1]
                                                                               .Trim() );
                }
            }
            else
            {
                _remoteWireFormatInfo.ReadCheckInterval = 0;
                _remoteWireFormatInfo.WriteCheckInterval = 0;
                _remoteWireFormatInfo.Version = 1.0f;
            }

            if ( _connectedResponseId != -1 )
            {
                var answer = new Response { CorrelationId = _connectedResponseId };
                SendCommand( answer );
                _connectedResponseId = -1;
            }
            else
                throw new IoException( "Received Connected Frame without a set Response Id for it." );

            return _remoteWireFormatInfo;
        }

        protected virtual ICommand ReadMessage( StompFrame frame )
        {
            Message message;
            frame.RemoveProperty( "transformation" );

            if ( frame.HasProperty( "content-length" ) )
                message = new BytesMessage { Content = frame.Content };
            else
                message = new TextMessage( Encoding.GetString( frame.Content, 0, frame.Content.Length ) );

            // Remove any receipt header we might have attached if the outbound command was
            // sent with response required set to true
            frame.RemoveProperty( "receipt" );

            // Clear any attached content length headers as they aren't needed anymore and can
            // clutter the Message Properties.
            frame.RemoveProperty( "content-length" );

            message.Type = frame.RemoveProperty( "type" );
            message.Destination = Destination.ConvertToDestination( frame.RemoveProperty( "destination" ) );
            message.ReplyTo = Destination.ConvertToDestination( frame.RemoveProperty( "reply-to" ) );
            message.TargetConsumerId = new ConsumerId( frame.RemoveProperty( "subscription" ) );
            message.CorrelationId = frame.RemoveProperty( "correlation-id" );
            message.MessageId = new MessageId( frame.RemoveProperty( "message-id" ) );
            message.Persistent = StompHelper.ToBool( frame.RemoveProperty( "persistent" ), false );

            // If it came from NMS.Stomp we added this header to ensure its reported on the
            // receiver side.
            if ( frame.HasProperty( "NMSXDeliveryMode" ) )
                message.Persistent = StompHelper.ToBool( frame.RemoveProperty( "NMSXDeliveryMode" ), false );

            if ( frame.HasProperty( "priority" ) )
                message.Priority = Byte.Parse( frame.RemoveProperty( "priority" ) );

            if ( frame.HasProperty( "timestamp" ) )
                message.Timestamp = Int64.Parse( frame.RemoveProperty( "timestamp" ) );

            if ( frame.HasProperty( "expires" ) )
                message.Expiration = Int64.Parse( frame.RemoveProperty( "expires" ) );

            if ( frame.RemoveProperty( "redelivered" ) != null )
                message.RedeliveryCounter = 1;

            // now lets add the generic headers
            foreach ( var key in frame.Properties.Keys )
            {
                var value = frame.Properties[key];
                message.Headers[key] = value;
            }

            return new MessageDispatch( message.TargetConsumerId, message.Destination, message, message.RedeliveryCounter );
        }

        protected virtual void SendCommand( ICommand command )
        {
            if ( Transport == null )
                Tracer.Fatal( "No transport configured so cannot return command: " + command );
            else
                Transport.Command( Transport, command );
        }

        protected virtual void WriteConnectionInfo( ConnectionInfo command, BinaryWriter dataOut )
        {
            // lets force a receipt for the Connect Frame.
            var frame = new StompFrame( "CONNECT", _encodeHeaders );

            frame.SetProperty( "client-id", command.ClientId );
            if ( command.UserName.IsNotEmpty() )
                frame.SetProperty( "login", command.UserName );
            if ( command.Password.IsNotEmpty() )
                frame.SetProperty( "passcode", command.Password );
            frame.SetProperty( "host", command.Host );
            frame.SetProperty( "accept-version", "1.0,1.1" );

            if ( MaxInactivityDuration != 0 )
                frame.SetProperty( "heart-beat", WriteCheckInterval + "," + ReadCheckInterval );

            _connectedResponseId = command.CommandId;

            frame.ToStream( dataOut );
        }

        protected virtual void WriteConsumerInfo( ConsumerInfo command, BinaryWriter dataOut )
        {
            var frame = new StompFrame( "SUBSCRIBE", _encodeHeaders );

            if ( command.ResponseRequired )
                frame.SetProperty( "receipt", command.CommandId );

            frame.SetProperty( "destination", Destination.ConvertToStompString( command.Destination ) );
            frame.SetProperty( "id", command.ConsumerId.ToString() );
            frame.SetProperty( "durable-subscriber-name", command.SubscriptionName );
            frame.SetProperty( "selector", command.Selector );
            frame.SetProperty( "ack", StompHelper.ToStomp( command.AckMode ) );

            if ( command.NoLocal )
                frame.SetProperty( "no-local", command.NoLocal.ToString() );

            // ActiveMQ extensions to STOMP
            frame.SetProperty( "transformation", command.Transformation ?? "jms-xml" );

            frame.SetProperty( "activemq.dispatchAsync", command.DispatchAsync );

            if ( command.Exclusive )
                frame.SetProperty( "activemq.exclusive", command.Exclusive );

            if ( command.SubscriptionName != null )
            {
                frame.SetProperty( "activemq.subscriptionName", command.SubscriptionName );
                // For an older 4.0 broker we need to set this header so they get the
                // subscription as well..
                frame.SetProperty( "activemq.subcriptionName", command.SubscriptionName );
            }

            frame.SetProperty( "activemq.maximumPendingMessageLimit", command.MaximumPendingMessageLimit );
            frame.SetProperty( "activemq.prefetchSize", command.PrefetchSize );
            frame.SetProperty( "activemq.priority", command.Priority );

            if ( command.Retroactive )
                frame.SetProperty( "activemq.retroactive", command.Retroactive );

            frame.ToStream( dataOut );
        }

        protected virtual void WriteKeepAliveInfo( KeepAliveInfo command, BinaryWriter dataOut )
        {
            var frame = new StompFrame( StompFrame.Keepalive, _encodeHeaders );

            frame.ToStream( dataOut );
        }

        protected virtual void WriteMessage( Message command, BinaryWriter dataOut )
        {
            var frame = new StompFrame( "SEND", _encodeHeaders );
            if ( command.ResponseRequired )
                frame.SetProperty( "receipt", command.CommandId );

            frame.SetProperty( "destination", Destination.ConvertToStompString( command.Destination ) );

            if ( command.ReplyTo != null )
                frame.SetProperty( "reply-to", Destination.ConvertToStompString( command.ReplyTo ) );
            if ( command.CorrelationId != null )
                frame.SetProperty( "correlation-id", command.CorrelationId );
            if ( command.Expiration != 0 )
                frame.SetProperty( "expires", command.Expiration );
            if ( command.Timestamp != 0 )
                frame.SetProperty( "timestamp", command.Timestamp );
            if ( command.Priority != 4 )
                frame.SetProperty( "priority", command.Priority );
            if ( command.Type != null )
                frame.SetProperty( "type", command.Type );
            if ( command.TransactionId != null )
                frame.SetProperty( "transaction", command.TransactionId.ToString() );

            frame.SetProperty( "persistent",
                               command.Persistent.ToString()
                                      .ToLower() );
            frame.SetProperty( "NMSXDeliveryMode",
                               command.Persistent.ToString()
                                      .ToLower() );

            if ( command.StompGroupId != null )
            {
                frame.SetProperty( "JMSXGroupID", command.StompGroupId );
                frame.SetProperty( "NMSXGroupID", command.StompGroupId );
                frame.SetProperty( "JMSXGroupSeq", command.StompGroupSeq );
                frame.SetProperty( "NMSXGroupSeq", command.StompGroupSeq );
            }

            // Perform any Content Marshaling.
            command.BeforeMarshall( this );

            // Store the Marshaled Content.
            frame.Content = command.Content;

            if ( command is BytesMessage )
            {
                if ( command.Content != null && command.Content.Length > 0 )
                    frame.SetProperty( "content-length", command.Content.Length );

                frame.SetProperty( "transformation", "jms-byte" );
            }

            // Marshal all properties to the Frame.
            var map = command.Headers;
            foreach ( var key in map.Keys )
                frame.SetProperty( key, map[key] );

            frame.ToStream( dataOut );
        }

        protected virtual void WriteMessageAck( MessageAck command, BinaryWriter dataOut )
        {
            var frame = new StompFrame( "ACK", _encodeHeaders );
            if ( command.ResponseRequired )
                frame.SetProperty( "receipt", "ignore:" + command.CommandId );

            frame.SetProperty( "message-id", command.LastMessageId.ToString() );
            frame.SetProperty( "subscription", command.ConsumerId.ToString() );

            if ( command.TransactionId != null )
                frame.SetProperty( "transaction", command.TransactionId.ToString() );

            frame.ToStream( dataOut );
        }

        protected virtual void WriteRemoveInfo( RemoveInfo command, BinaryWriter dataOut )
        {
            var frame = new StompFrame( "UNSUBSCRIBE", _encodeHeaders );
            Object id = command.ObjectId;

            if ( !( id is ConsumerId ) )
                return;
            var consumerId = id as ConsumerId;
            if ( command.ResponseRequired )
                frame.SetProperty( "receipt", command.CommandId );
            frame.SetProperty( "id", consumerId.ToString() );

            frame.ToStream( dataOut );
        }

        protected virtual void WriteShutdownInfo( ShutdownInfo command, BinaryWriter dataOut )
        {
            Debug.Assert( !command.ResponseRequired );

            var frame = new StompFrame( "DISCONNECT", _encodeHeaders );

            frame.ToStream( dataOut );
        }

        protected virtual void WriteTransactionInfo( TransactionInfo command, BinaryWriter dataOut )
        {
            String type;
            switch ( (TransactionType) command.Type )
            {
                case TransactionType.Commit:
                    command.ResponseRequired = true;
                    type = "COMMIT";
                    break;
                case TransactionType.Rollback:
                    command.ResponseRequired = true;
                    type = "ABORT";
                    break;
                case TransactionType.Begin:
                    type = "BEGIN";
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var frame = new StompFrame( type, _encodeHeaders );
            if ( command.ResponseRequired )
                frame.SetProperty( "receipt", command.CommandId );

            frame.SetProperty( "transaction", command.TransactionId.ToString() );

            frame.ToStream( dataOut );
        }
    }
}