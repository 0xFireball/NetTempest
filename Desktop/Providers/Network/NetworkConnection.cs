﻿//
// NetworkConnection.cs
//
// Author:
//   Eric Maupin <me@ermau.com>
//
// Copyright (c) 2011 Eric Maupin
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Tempest.InternalProtocol;
using System.Threading;

#if NET_4
using System.Collections.Concurrent;
#endif

namespace Tempest.Providers.Network
{
	public abstract class NetworkConnection
		: IConnection
	{
		protected NetworkConnection (IEnumerable<Protocol> protocols, Func<IPublicKeyCrypto> publicKeyCryptoFactory, IAsymmetricKey authKey, bool generateKey)
		{
			if (protocols == null)
				throw new ArgumentNullException ("protocols");
			if (publicKeyCryptoFactory == null)
				throw new ArgumentNullException ("publicKeyCrypto");

			this.authenticationKey = authKey;
			this.requiresHandshake = protocols.Any (p => p.RequiresHandshake);
			if (this.requiresHandshake)
			{
				this.publicKeyCryptoFactory = publicKeyCryptoFactory;

				ThreadPool.QueueUserWorkItem (s =>
				{
					this.pkAuthentication = this.publicKeyCryptoFactory();

					if (this.authenticationKey == null)
					{
						if (generateKey)
						{
							this.publicAuthenticationKey = this.pkAuthentication.ExportKey (false);
							this.authenticationKey = this.pkAuthentication.ExportKey (true);
						}
					}
					else
					{
						this.pkAuthentication.ImportKey (authKey);
						this.publicAuthenticationKey = this.pkAuthentication.ExportKey (false);
					}

					this.authReady = true;
				});
			}

			this.protocols = new Dictionary<byte, Protocol>();
			foreach (Protocol p in protocols)
			{
				if (p == null)
					throw new ArgumentNullException ("protocols", "protocols contains a null protocol");
				if (this.protocols.ContainsKey (p.id))
					throw new ArgumentException ("Only one version of a protocol may be specified");

				this.protocols.Add (p.id, p);
			}

			this.protocols[1] = TempestMessage.InternalProtocol;
			
			#if TRACE
			this.connectionId = Interlocked.Increment (ref nextConnectionId);
			this.typeName = GetType().Name;
			#endif
		}

		/// <summary>
		/// Raised when a message is received.
		/// </summary>
		public event EventHandler<MessageEventArgs> MessageReceived;

		/// <summary>
		/// Raised when a message has completed sending.
		/// </summary>
		public event EventHandler<MessageEventArgs> MessageSent;

		/// <summary>
		/// Raised when the connection is lost or manually disconnected.
		/// </summary>
		public event EventHandler<DisconnectedEventArgs> Disconnected;

		public bool IsConnected
		{
			get
			{
				lock (this.stateSync)
					return (!this.disconnecting && this.reliableSocket != null && this.reliableSocket.Connected);
			}
		}

		public IEnumerable<Protocol> Protocols
		{
			get { return this.protocols.Values; }
		}

		public int ResponseTime
		{
			get;
			private set;
		}

		public MessagingModes Modes
		{
			get { return MessagingModes.Async; }
		}

		public EndPoint RemoteEndPoint
		{
			get;
			protected set;
		}

		public IAsymmetricKey PublicAuthenticationKey
		{
			get { return this.publicAuthenticationKey; }
		}

		public IEnumerable<MessageEventArgs> Tick()
		{
			throw new NotSupportedException();
		}

		public virtual void Send (Message message)
		{
			if (message == null)
				throw new ArgumentNullException ("message");
			if (!IsConnected)
				return;

			SocketAsyncEventArgs eargs = null;
			#if NET_4
			if (!writerAsyncArgs.TryPop (out eargs))
			{
				while (eargs == null)
				{
					int count = bufferCount;
					if (count == BufferLimit)
					{
						SpinWait wait = new SpinWait();
						while (!writerAsyncArgs.TryPop (out eargs))
							wait.SpinOnce();

						eargs.AcceptSocket = null;
					}
					else if (count == Interlocked.CompareExchange (ref bufferCount, count + 1, count))
					{
						eargs = new SocketAsyncEventArgs();
						eargs.SetBuffer (new byte[1024], 0, 1024);
					}
				}
			}
			else
				eargs.AcceptSocket = null;
			#else
			while (eargs == null)
			{
				lock (writerAsyncArgs)
				{
					if (writerAsyncArgs.Count != 0)
					{
						eargs = writerAsyncArgs.Pop();
						#if !SILVERLIGHT
						eargs.AcceptSocket = null;
						#endif
					}
					else
					{
						if (bufferCount != BufferLimit)
						{
							bufferCount++;
							eargs = new SocketAsyncEventArgs();
							eargs.SetBuffer (new byte[1024], 0, 1024);
						}
					}
				}
			}
			#endif

			int length;
			byte[] buffer = GetBytes (message, out length, eargs.Buffer);

			eargs.SetBuffer (buffer, 0, length);
			eargs.UserToken = message;

			bool sent;
			lock (this.stateSync)
			{
				if (!IsConnected)
				{
					#if !NET_4
					lock (writerAsyncArgs)
					#endif
					writerAsyncArgs.Push (eargs);

					return;
				}
				
				eargs.Completed += ReliableSendCompleted;
				int p = Interlocked.Increment (ref this.pendingAsync);
				Trace.WriteLine (String.Format ("Increment pending: {0}", p), String.Format ("{1}:{2} Send({0})", message, this.typeName, this.connectionId));
				sent = !this.reliableSocket.SendAsync (eargs);
			}

			if (sent)
				ReliableSendCompleted (this.reliableSocket, eargs);
		}

		public void DisconnectAsync()
		{
			Disconnect (false);
		}

		public void DisconnectAsync (ConnectionResult reason, string customReason = null)
		{
			Disconnect (false, reason, customReason);
		}

		public void Disconnect()
		{
			Disconnect (true);
		}

		public void Disconnect (ConnectionResult reason, string customReason = null)
		{
			Disconnect (true, reason, customReason);
		}

		public void Dispose()
		{
			Dispose (true);
		}

		protected bool authReady;
		protected bool disposed;

		private const int BaseHeaderLength = 7;
		private int maxMessageLength = 1048576;

		#if TRACE
		protected int connectionId;
		#endif

		protected Dictionary<byte, Protocol> protocols;
		protected bool requiresHandshake;

		protected AesManaged aes;
		protected HMACSHA256 hmac;

		protected string signingHashAlgorithm = "SHA256";
		protected readonly Func<IPublicKeyCrypto> publicKeyCryptoFactory;

		protected IPublicKeyCrypto pkAuthentication;
		protected IAsymmetricKey authenticationKey;
		protected IAsymmetricKey publicAuthenticationKey;

		protected readonly object stateSync = new object();
		protected int pendingAsync = 0;
		protected bool disconnecting = false;
		protected bool formallyConnected = false;
		protected ConnectionResult disconnectingReason;
		protected string disconnectingCustomReason;

		protected Socket reliableSocket;

		protected byte[] rmessageBuffer = new byte[20480];
		protected BufferValueReader rreader;
		private int rmessageOffset = 0;
		private int rmessageLoaded = 0;

		internal int NetworkId
		{
			get;
			set;
		}

		protected void Dispose (bool disposing)
		{
			if (this.disposed)
				return;

			this.disposed = true;
			Disconnect (true);

			Trace.WriteLine (String.Format ("Waiting for {0} pending asyncs", this.pendingAsync), String.Format ("{0}:{1} Dispose()", this.typeName, connectionId));
			while (this.pendingAsync > 0)
				Thread.Sleep (1);

			Trace.WriteLine ("Disposed", String.Format ("{0}:{1} Dispose()", this.typeName, connectionId));
		}

		protected virtual void Recycle()
		{
			lock (this.stateSync)
			{
				NetworkId = 0;

				if (this.hmac != null)
				{
					((IDisposable)this.hmac).Dispose();
					this.hmac = null;
				}

				if (this.aes != null)
				{
					((IDisposable)this.aes).Dispose();
					this.aes = null;
				}

				this.reliableSocket = null;
				this.rmessageOffset = 0;
				this.rmessageLoaded = 0;
			}
		}

		protected virtual void OnMessageReceived (MessageEventArgs e)
		{
			var mr = this.MessageReceived;
			if (mr != null)
				mr (this, e);
		}

		protected virtual void OnDisconnected (DisconnectedEventArgs e)
		{
			var dc = this.Disconnected;
			if (dc != null)
				dc (this, e);
		}

		protected virtual void OnMessageSent (MessageEventArgs e)
		{
			var sent = this.MessageSent;
			if (sent != null)
				sent (this, e);
		}

		protected void EncryptMessage (BufferValueWriter writer, ref int headerLength)
		{
			if (this.aes == null)
				throw new InvalidOperationException ("Attempting to encrypt a message without an encryptor");

			ICryptoTransform encryptor = null;
			byte[] iv = null;
			lock (this.aes)
			{
				this.aes.GenerateIV();
				iv = this.aes.IV;
				encryptor = this.aes.CreateEncryptor();
			}

			int r = ((writer.Length - BaseHeaderLength) % encryptor.OutputBlockSize);
			if (r != 0)
				writer.Pad (encryptor.OutputBlockSize - r);

			byte[] payload = encryptor.TransformFinalBlock (writer.Buffer, BaseHeaderLength, writer.Length - BaseHeaderLength);

			writer.Length = BaseHeaderLength;
			writer.InsertBytes (BaseHeaderLength, iv, 0, iv.Length);
			writer.WriteBytes (payload);

			headerLength += iv.Length;
		}

		protected void DecryptMessage (MessageHeader header, ref BufferValueReader r, ref byte[] message, ref int moffset)
		{
			byte[] payload = r.ReadBytes();

			ICryptoTransform decryptor;
			lock (this.aes)
			{
				this.aes.IV = header.IV;
				decryptor = this.aes.CreateDecryptor();
			}

			message = decryptor.TransformFinalBlock (payload, 0, payload.Length);
			moffset = 0;

			r = new BufferValueReader (message);
		}

		protected virtual void SignMessage (string hashAlg, BufferValueWriter writer, int headerLength)
		{
			if (this.hmac == null)
				throw new InvalidOperationException();

			writer.WriteBytes (this.hmac.ComputeHash (writer.Buffer, headerLength, writer.Length - headerLength));
		}

		protected virtual bool VerifyMessage (string hashAlg, Message message, byte[] signature, byte[] data, int moffset, int length)
		{
			byte[] ourhash = this.hmac.ComputeHash (data, moffset, length);

			if (signature.Length != ourhash.Length)
				return false;

			for (int i = 0; i < signature.Length; i++)
			{
				if (signature[i] != ourhash[i])
					return false;
			}

			return true;
		}

		protected byte[] GetBytes (Message message, out int length, byte[] buffer)
		{
			BufferValueWriter writer = new BufferValueWriter (buffer);
			writer.WriteByte (message.Protocol.id);
			writer.WriteUInt16 (message.MessageType);
			writer.Length += sizeof (int); // length  placeholder

			var context = new SerializationContext (this, this.protocols[message.Protocol.id], new TypeMap());

			message.WritePayload (context, writer);

			int headerLength = BaseHeaderLength;

			var types = context.GetNewTypes().OrderBy (kvp => kvp.Value).ToList();
			if (types.Count > 0)
			{
				if (types.Count > Int16.MaxValue)
					throw new ArgumentException ("Too many different types for serialization");

				int payloadLen = writer.Length;
				byte[] payload = writer.Buffer;
				writer = new BufferValueWriter (new byte[1024 + writer.Length]);
				writer.WriteByte (message.Protocol.id);
				writer.WriteUInt16 (message.MessageType);
				writer.Length += sizeof (int);
				writer.WriteUInt16 ((ushort)types.Count);
				for (int i = 0; i < types.Count; ++i)
					writer.WriteString (types[i].Key.GetSimpleName());

				headerLength = writer.Length;

				Buffer.BlockCopy (payload, BaseHeaderLength, writer.Buffer, headerLength, payloadLen - BaseHeaderLength);
			}

			if (message.Encrypted)
				EncryptMessage (writer, ref headerLength);

			if (message.Authenticated)
				SignMessage (this.signingHashAlgorithm, writer, headerLength);

			byte[] rawMessage = writer.Buffer;
			length = writer.Length;
			int len = length << 1;
			if (types.Count > 0)
				len |= 1; // serialization header

			Buffer.BlockCopy (BitConverter.GetBytes (len), 0, rawMessage, BaseHeaderLength - sizeof(int), sizeof(int));

			return rawMessage;
		}

		/// <returns><c>true</c> if there was sufficient data to retrieve the message's header.</returns>
		/// <remarks>
		/// If <see cref="TryGetHeader"/> returns <c>true</c> and <paramref name="header"/> is <c>null</c>,
		/// disconnect.
		/// </remarks>
		protected bool TryGetHeader (byte[] buffer, int offset, int remaining, out MessageHeader header)
		{
			#if TRACE
			int c = Interlocked.Increment (ref nextCallId);
			#endif

			header = null;

			Trace.WriteLine ("Entering", String.Format ("{0}:{5} {1}:TryGetHeader({2},{3},{4})", this.typeName, c, buffer.Length, offset,
			                                remaining, connectionId));

			BufferValueReader reader = new BufferValueReader (buffer, offset + 1, remaining);

			ushort type;
			int mlen;
			try
			{
				type = reader.ReadUInt16();
				mlen = reader.ReadInt32();
				bool hasTypeHeader = (mlen & 1) == 1;
				mlen >>= 1;
				
				Protocol p;
				if (!this.protocols.TryGetValue (buffer[offset], out p))
				{
					Trace.WriteLine ("Exiting (Protocol " + buffer[offset] + " not found)", String.Format ("{0}:{5} {1}:TryGetHeader({2},{3},{4})", this.typeName, c, buffer.Length, offset,
			                                remaining, connectionId));
					return true;
				}

				Message msg = p.Create (type);
				if (msg == null)
				{
					Trace.WriteLine ("Exiting (Message " + type + " not found)", String.Format ("{0}:{5} {1}:TryGetHeader({2},{3},{4})", this.typeName, c, buffer.Length, offset,
			                                remaining, connectionId));
					return true;
				}

				Trace.WriteLine (String.Format ("Have {0} ({1:N0})", msg.GetType().Name, mlen), String.Format ("{0}:{5} {1}:TryGetHeader({2},{3},{4})", this.typeName, c, buffer.Length, offset,
			                                remaining, connectionId));

				int headerLength = BaseHeaderLength;

				TypeMap map;
				if (hasTypeHeader)
				{
					Trace.WriteLine ("Has type header, reading types", String.Format ("{0}:{5} {1}:TryGetHeader({2},{3},{4})", this.typeName, c, buffer.Length, offset,
					                                remaining, connectionId));

					ushort numTypes = reader.ReadUInt16();
					var types = new Dictionary<Type, ushort> (numTypes);
					for (ushort i = 0; i < numTypes; ++i)
						types[Type.GetType (reader.ReadString())] = i;

					headerLength = reader.Position - offset;
					map = new TypeMap (types);
				}
				else
					map = new TypeMap();

				var context = new SerializationContext (this, p, map);

				byte[] iv = null;
				if (msg.Encrypted && this.aes != null)
				{
					int length = this.aes.IV.Length;
					iv = new byte[length];

					headerLength += length;
					if (remaining < headerLength)
					{
						Trace.WriteLine ("Exiting (message not buffered)", String.Format ("{0}:{5} {1}:TryGetHeader({2},{3},{4})", this.typeName, c, buffer.Length, offset,
			                                remaining, connectionId));
						return false;
					}

					Buffer.BlockCopy (buffer, reader.Position, iv, 0, length);
					reader.Position += length;
				}

				Trace.WriteLine ("Exiting", String.Format ("{0}:{5} {1}:TryGetHeader({2},{3},{4})", this.typeName, c, buffer.Length, offset,
			                                remaining, connectionId));

				header = new MessageHeader (p, msg, mlen, headerLength, context, iv);
				return true;
			}
			catch (Exception ex)
			{
				header = null;
				return true;
			}
		}

		private void BufferMessages (ref byte[] buffer, ref int bufferOffset, ref int messageOffset, ref int remainingData, ref BufferValueReader reader)
		{
			#if TRACE
			int c = Interlocked.Increment (ref nextCallId);
			#endif
			Trace.WriteLine ("Entering", String.Format ("{0}:{6} {1}:BufferMessages({2},{3},{4},{5})", this.typeName, c, buffer.Length, bufferOffset, messageOffset, remainingData, connectionId));

			this.lastReceived = DateTime.Now;

			int length = 0;
			while (remainingData >= BaseHeaderLength)
			{
				MessageHeader header;
				if (!TryGetHeader (buffer, messageOffset, remainingData, out header))
				{
					Trace.WriteLine ("Failed to get header",
					                 String.Format ("{0}:{6} {1}:BufferMessages({2},{3},{4},{5})", this.typeName, c, buffer.Length,
					                                bufferOffset, messageOffset, remainingData, connectionId));
					break;
				}

				if (header == null)
				{
					Disconnect (true);
					Trace.WriteLine ("Exiting (header not found)",
					                 String.Format ("{0}:{6} {1}:BufferMessages({2},{3},{4},{5})", this.typeName, c, buffer.Length,
					                                bufferOffset, messageOffset, remainingData, connectionId));
					return;
				}

				length = header.MessageLength;
				if (length > maxMessageLength)
				{
					Disconnect (true);
					Trace.WriteLine ("Exiting (bad message size)",
					                 String.Format ("{0}:{6} {1}:BufferMessages({2},{3},{4},{5})", this.typeName, c, buffer.Length,
					                                bufferOffset, messageOffset, remainingData, connectionId));
					return;
				}

				if (remainingData < length)
				{
					Trace.WriteLine ("Message not fully received",
					                 String.Format ("{0}:{6} {1}:BufferMessages({2},{3},{4},{5})", this.typeName, c, buffer.Length,
					                                bufferOffset, messageOffset, remainingData, connectionId));
					bufferOffset += remainingData;
					break;
				}

				if (!IsConnected)
					return;

				try
				{
					int moffset = messageOffset + header.HeaderLength;
					byte[] message = buffer;
					BufferValueReader r = reader;

					r.Position = moffset;
					if (header.Message.Encrypted)
						DecryptMessage (header, ref r, ref message, ref moffset);

					r.Position = moffset;
					header.Message.ReadPayload (header.SerializationContext, r);

					if (header.Message.Authenticated && this.requiresHandshake)
					{
						byte[] signature = reader.ReadBytes(); // Need the original reader here, sig is after payload
						if (!VerifyMessage (this.signingHashAlgorithm, header.Message, signature, buffer, messageOffset + header.HeaderLength, header.MessageLength - header.HeaderLength - signature.Length - sizeof(int)))
						{
							Disconnect (true, ConnectionResult.MessageAuthenticationFailed);
							Trace.WriteLine ("Exiting (message auth failed)",
											 String.Format ("{0}:{6} {1}:BufferMessages({2},{3},{4},{5})", this.typeName, c, buffer.Length,
															bufferOffset, messageOffset, remainingData, connectionId));
							return;
						}
					}
				}
				catch (Exception ex)
				{
					Disconnect (true);
					Trace.WriteLine ("Exiting for error: " + ex,
					                 String.Format ("{0}:{6} {1}:BufferMessages({2},{3},{4},{5})", this.typeName, c, buffer.Length,
					                                bufferOffset, messageOffset, remainingData, connectionId));
					return;
				}

				var tmessage = (header.Message as TempestMessage);
				if (tmessage == null)
					OnMessageReceived (new MessageEventArgs (this, header.Message));
				else
					OnTempestMessageReceived (new MessageEventArgs (this, header.Message));

				messageOffset += length;
				bufferOffset = messageOffset;
				remainingData -= length;
			}

			if (remainingData > 0 || messageOffset + BaseHeaderLength >= buffer.Length)
			{
				byte[] newBuffer = new byte[(length > buffer.Length) ? length : buffer.Length];
				reader = new BufferValueReader (newBuffer, 0, newBuffer.Length);
				Buffer.BlockCopy (buffer, messageOffset, newBuffer, 0, remainingData);
				buffer = newBuffer;
				bufferOffset = remainingData;
				messageOffset = 0;
			}

			Trace.WriteLine ("Exiting", String.Format ("{0}:{6} {1}:BufferMessages({2},{3},{4},{5})", this.typeName, c, buffer.Length, bufferOffset, messageOffset, remainingData, connectionId));
		}

		protected void ReliableReceiveCompleted (object sender, SocketAsyncEventArgs e)
		{
			#if TRACE
			int c = Interlocked.Increment (ref nextCallId);
			#endif

			int p;
			Trace.WriteLine ("Entering", String.Format ("{2}:{4} {3}:ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
			bool async;
			do
			{
				if (e.BytesTransferred == 0 || e.SocketError != SocketError.Success)
				{
					Disconnect (true);
					p = Interlocked.Decrement (ref this.pendingAsync);
					Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2}:{4} {3}:ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
					Trace.WriteLine ("Exiting (error)", String.Format ("{2}:{4} {3}:ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
					return;
				}

				p = Interlocked.Decrement (ref this.pendingAsync);
				Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2}:{4} {3}:ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));

				this.rmessageLoaded += e.BytesTransferred;
				lock (this.stateSync)
				{
					int bufferOffset = e.Offset;
					BufferMessages (ref this.rmessageBuffer, ref bufferOffset, ref this.rmessageOffset, ref this.rmessageLoaded,
									ref this.rreader);
					e.SetBuffer (this.rmessageBuffer, bufferOffset, this.rmessageBuffer.Length - bufferOffset);

					if (!IsConnected)
					{
						Trace.WriteLine ("Exiting (not connected)", String.Format ("{2}:{4} {3}:ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
						return;
					}

					p = Interlocked.Increment (ref this.pendingAsync);
					Trace.WriteLine (String.Format ("Increment pending: {0}", p), String.Format ("{2}:{4} {3}:ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));

					async = this.reliableSocket.ReceiveAsync (e);
				}
			} while (!async);

			Trace.WriteLine ("Exiting", String.Format ("{2}:{4} {3}:ReliableReceiveCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
		}

		protected long lastSent;
		protected DateTime lastReceived;
		protected int pingsOut = 0;

		protected virtual void OnTempestMessageReceived (MessageEventArgs e)
		{
			switch (e.Message.MessageType)
			{
				case (ushort)TempestMessageType.Ping:
					Send (new PongMessage());
					
					#if !SILVERLIGHT
					this.lastSent = Stopwatch.GetTimestamp();
					#else
					this.lastSent = DateTime.Now.Ticks;
					#endif
					break;

				case (ushort)TempestMessageType.Pong:
					#if !SILVERLIGHT
					long timestamp = Stopwatch.GetTimestamp();
					#else
					long timestamp = DateTime.Now.Ticks;
					#endif
					
					ResponseTime = (int)Math.Round (TimeSpan.FromTicks (timestamp - this.lastSent).TotalMilliseconds, 0);
					this.pingsOut = 0;
					break;

				case (ushort)TempestMessageType.Disconnect:
					var msg = (DisconnectMessage)e.Message;
					Disconnect (true, msg.Reason, msg.CustomReason);
					break;
			}
		}

		private void Disconnect (bool now, ConnectionResult reason = ConnectionResult.FailedUnknown, string customReason = null)
		{
			#if TRACE
			int c = Interlocked.Increment (ref nextCallId);
			#endif
			Trace.WriteLine (String.Format ("Entering {0}", new Exception().StackTrace), String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, GetType().Name, c, connectionId));

			if (this.disconnecting || this.reliableSocket == null)
			{
				Trace.WriteLine ("Already disconnected, exiting", String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));
				return;
			}

			lock (this.stateSync)
			{
				Trace.WriteLine ("Got state lock.", String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));

				if (this.disconnecting || this.reliableSocket == null)
				{
					Trace.WriteLine ("Already disconnected, exiting", String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));
					return;
				}

				this.disconnecting = true;

				if (!this.reliableSocket.Connected)
				{
					Trace.WriteLine ("Socket not connected, finishing cleanup.", String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));

					Recycle();

					while (this.pendingAsync > 1) // If called from *Completed, there'll be a pending.
						Thread.Sleep (0);

					this.disconnecting = false;
				}
				else if (now)
				{
					Trace.WriteLine ("Shutting down socket.", String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));

					#if !SILVERLIGHT
					this.reliableSocket.Shutdown (SocketShutdown.Both);
					this.reliableSocket.Disconnect (true);
					#else
					this.reliableSocket.Close();
					#endif
					Recycle();

					Trace.WriteLine (String.Format ("Waiting for pending ({0}) async.", pendingAsync), String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));

					while (this.pendingAsync > 1)
						Thread.Sleep (0);

					Trace.WriteLine ("Finished waiting, raising Disconnected.", String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));

					this.disconnecting = false;
				}
				else
				{
					Trace.WriteLine ("Disconnecting asynchronously.", String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));

					this.disconnectingReason = reason;
					this.disconnectingCustomReason = customReason;

					int p = Interlocked.Increment (ref this.pendingAsync);
					Trace.WriteLine (String.Format ("Increment pending: {0}", p), String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));

					ThreadPool.QueueUserWorkItem (s =>
					{
						Trace.WriteLine (String.Format ("Async DC waiting for pending ({0}) async.", pendingAsync), String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));

						while (this.pendingAsync > 2) // Disconnect is pending.
							Thread.Sleep (0);

						Trace.WriteLine ("Finished waiting, disconnecting async.", String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));

						#if !SILVERLIGHT
						var args = new SocketAsyncEventArgs();// { DisconnectReuseSocket = true };
						args.Completed += OnDisconnectCompleted;
						
						if (!this.reliableSocket.DisconnectAsync (args))				
							OnDisconnectCompleted (this.reliableSocket, args);
						#else
						this.reliableSocket.Close();
						#endif
					});

					return;
				}
			}

			OnDisconnected (new DisconnectedEventArgs (this, reason, customReason));
			Trace.WriteLine ("Raised Disconnected, exiting", String.Format ("{2}:{4} {3}:Disconnect({0},{1})", now, reason, this.typeName, c, connectionId));
		}

		private void ReliableSendCompleted (object sender, SocketAsyncEventArgs e)
		{
			#if TRACE
			int c = Interlocked.Increment (ref nextCallId);
			#endif
			Trace.WriteLine ("Entering", String.Format ("{2}:{4} {3}:ReliableSendCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));

			e.Completed -= ReliableSendCompleted;

			var message = (Message)e.UserToken;

			#if !NET_4
			lock (writerAsyncArgs)
			#endif
			writerAsyncArgs.Push (e);

			int p;
			if (e.BytesTransferred == 0 || e.SocketError != SocketError.Success)
			{
				Disconnect (true);
				p = Interlocked.Decrement (ref this.pendingAsync);
				Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2}:{4} {3}:ReliableSendCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
				Trace.WriteLine ("Exiting (error)", String.Format ("{2}:{4} {3}:ReliableSendCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
				return;
			}

			if (!(message is TempestMessage))
				OnMessageSent (new MessageEventArgs (this, message));

			p = Interlocked.Decrement (ref this.pendingAsync);
			Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2}:{4} {3}:ReliableSendCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
			Trace.WriteLine ("Exiting", String.Format ("{2}:{4} {3}:ReliableSendCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
		}

		private void OnDisconnectCompleted (object sender, SocketAsyncEventArgs e)
		{
			#if TRACE
			int c = Interlocked.Increment (ref nextCallId);
			#endif
			Trace.WriteLine ("Entering", String.Format ("{2}:{4} {3}:OnDisconnectCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));

			lock (this.stateSync)
			{
				Trace.WriteLine ("Got lock", String.Format ("{2}:{4} {3}:OnDisconnectCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));

				this.disconnecting = false;
				Recycle();
			}

			Trace.WriteLine ("Raising Disconnected", String.Format ("{2}:{4} {3}:OnDisconnectCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));

			OnDisconnected (new DisconnectedEventArgs (this, this.disconnectingReason, this.disconnectingCustomReason));
			int p = Interlocked.Decrement (ref this.pendingAsync);
			Trace.WriteLine (String.Format ("Decrement pending: {0}", p), String.Format ("{2}:{4} {3}:OnDisconnectCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
			Trace.WriteLine ("Exiting", String.Format ("{2}:{4} {3}:OnDisconnectCompleted({0},{1})", e.BytesTransferred, e.SocketError, this.typeName, c, connectionId));
		}

		// TODO: Better buffer limit
		private static readonly int BufferLimit = Environment.ProcessorCount * 10;
		private static volatile int bufferCount = 0;

		#if TRACE
		protected static int nextCallId = 0;
		protected static int nextConnectionId;
		protected readonly string typeName;
		#endif

		#if NET_4
		private static readonly ConcurrentStack<SocketAsyncEventArgs> writerAsyncArgs = new ConcurrentStack<SocketAsyncEventArgs>();
		#else
		private static readonly Stack<SocketAsyncEventArgs> writerAsyncArgs = new Stack<SocketAsyncEventArgs>();
		#endif
	}
}
