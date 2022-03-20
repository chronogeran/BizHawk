using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BizHawk.Client.Common
{
	public sealed class SocketServer
	{
		private IPEndPoint _remoteEp;

		private Socket _soc = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		private readonly Dictionary<int, Socket> _clients = new();
		private int _currentClientSocketHandle = 1;

		private readonly Func<byte[]> _takeScreenshotCallback;

		private (string HostIP, int Port) _targetAddr;

		public bool Connected { get; private set; }

		public string IP
		{
			get => _targetAddr.HostIP;
			set
			{
				_targetAddr.HostIP = value;
				Connect();
			}
		}

		public int Port
		{
			get => _targetAddr.Port;
			set
			{
				_targetAddr.Port = value;
				Connect();
			}
		}

		public (string HostIP, int Port) TargetAddress
		{
			get => _targetAddr;
			set
			{
				_targetAddr = value;
				Connect();
			}
		}

#if true
		private const int Retries = 10;
#else
		public int Retries { get; set; } = 10;
#endif

		public bool Successful { get; private set; }

		public SocketServer(Func<byte[]> takeScreenshotCallback, string ip, int port)
		{
			_takeScreenshotCallback = takeScreenshotCallback;
			TargetAddress = (ip, port);
		}

		private void Connect()
		{
			try
			{
				_remoteEp = new IPEndPoint(IPAddress.Parse(_targetAddr.HostIP), _targetAddr.Port);
				_soc = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
				_soc.Connect(_remoteEp);
			}
			catch
			{
				return;
			}
			Connected = true;
		}

		public void Listen(int backlog)
		{
			_remoteEp = new IPEndPoint(_targetAddr.HostIP == "*" ? IPAddress.Any : IPAddress.Parse(_targetAddr.HostIP), _targetAddr.Port);
			_soc = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			_soc.Blocking = false;
			_soc.Bind(_remoteEp);
			_soc.Listen(backlog);
		}

		public int Accept()
		{
			try
			{
				var newClient = _soc.Accept();
				if (newClient != null)
				{
					var handle = _currentClientSocketHandle++;
					_clients.Add(handle, newClient);
					return handle;
				}
			}
			catch
			{
				// No incoming connection
			}
			return 0;
		}

		public string GetInfo() => $"{_targetAddr.HostIP}:{_targetAddr.Port}";

		private Socket GetSocket(int socketHandle) { return socketHandle == 0 ? _soc : _clients[socketHandle]; }

		public string ReceiveString(Encoding encoding = null, int socketHandle = 0)
		{
			if (!Connected)
			{
				Connect();
			}

			var myencoding = encoding ?? Encoding.UTF8;
			var socket = GetSocket(socketHandle);

			try
			{
				//build length of string into a string
				byte[] oneByte = new byte[1];
				StringBuilder sb = new StringBuilder();
				for (; ; )
				{
					int recvd = socket.Receive(oneByte, 1, 0);
					if (oneByte[0] == (byte)' ')
						break;
					sb.Append((char)oneByte[0]);
				}

				//receive string of indicated length
				int lenStringBytes = int.Parse(sb.ToString());
				byte[] buf = new byte[lenStringBytes];
				int todo = lenStringBytes;
				int at = 0;
				for (; ; )
				{
					int recvd = socket.Receive(buf, at, todo, SocketFlags.None);
					if (recvd == 0)
						throw new InvalidOperationException("ReceiveString terminated early");
					todo -= recvd;
					at += recvd;
					if (todo == 0)
						break;
				}
				return myencoding.GetString(buf, 0, lenStringBytes);
			}
			catch
			{
				//not sure I like this, but that's how it was
				return "";
			}
		}

		public int SendBytes(byte[] sendBytes, int socketHandle = 0)
		{
			try
			{
				var socket = GetSocket(socketHandle);
				return socket.Send(sendBytes);
			}
			catch
			{
				return -1;
			}
		}

		public string SendScreenshot(int waitingTime = 0, int socketHandle = 0)
		{
			var bmpBytes = _takeScreenshotCallback();
			var sentBytes = 0;
			var tries = 0;
			while (sentBytes <= 0 && tries < Retries)
			{
				try
				{
					tries++;
					sentBytes = SendBytes(bmpBytes, socketHandle);
				}
				catch (SocketException)
				{
					Connect();
					sentBytes = 0;
				}
				if (sentBytes == -1)
				{
					Connect();
				}
			}

			Successful = tries < Retries;
			if (waitingTime == 0)
			{
				return Successful ? "Screenshot was sent" : "Screenshot could not be sent";
			}
			var resp = ReceiveString(socketHandle: socketHandle);
			return resp == "" ? "Failed to get a response" : resp;
		}

		public int SendString(string sendString, Encoding encoding = null, int socketHandle = 0)
		{
			var payloadBytes = (encoding ?? Encoding.UTF8).GetBytes(sendString);
			var strLenOfPayloadBytes = payloadBytes.Length.ToString();
			var strLenOfPayloadBytesAsBytes = Encoding.ASCII.GetBytes(strLenOfPayloadBytes);
			
			System.IO.MemoryStream ms = new System.IO.MemoryStream();
			ms.Write(strLenOfPayloadBytesAsBytes, 0, strLenOfPayloadBytesAsBytes.Length);
			ms.WriteByte((byte)' ');
			ms.Write(payloadBytes,0,payloadBytes.Length);
			
			int sentBytes = SendBytes(ms.ToArray(), socketHandle);

			Successful = sentBytes > 0;
			return sentBytes;
		}

		public void SetTimeout(int timeout, int socketHandle = 0)
		{
			var socket = GetSocket(socketHandle);
			socket.ReceiveTimeout = timeout;
		}

		public void SocketConnected() => Connected = !_soc.Poll(1000, SelectMode.SelectRead) || _soc.Available != 0;
	}
}
