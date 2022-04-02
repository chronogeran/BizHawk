using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BizHawk.Client.Common
{
	public sealed class RawSockets
	{
		private readonly Dictionary<int, Socket> _sockets = new();
		private int _currentClientSocketHandle = 1;

		private Socket GetSocket(int socketHandle) { return _sockets.ContainsKey(socketHandle) ? _sockets[socketHandle] : null; }

		public int NewSocket()
		{
			var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			var handle = _currentClientSocketHandle++;
			_sockets[handle] = socket;
				//socket.Blocking = false;
			return handle;
		}

		public string Destroy(int handle)
		{
			var socket = GetSocket(handle);
			if (socket == null)
				return "socketnotfound";

			socket.Disconnect(false);
			socket.Dispose();
			_sockets.Remove(handle);
			return null;
		}

		public string Connect(int handle, string host, int port)
		{
			var socket = GetSocket(handle);
			if (socket == null)
				return "socketnotfound";
			try
			{
				// todo can be non-blocking before connect?
				socket.Connect(host, port);
				socket.Blocking = false;
			}
			catch (Exception ex)
			{
				// todo return errors
				return ex.Message;
			}
			return null;
		}

		public string Bind(int handle, string host, int port)
		{
			var endpoint = new IPEndPoint(host == "*" ? IPAddress.Any : IPAddress.Parse(host), port);
			var socket = GetSocket(handle);
			if (socket == null)
				return "socketnotfound";
			try
			{
				socket.Bind(endpoint);
			}
			catch (Exception ex)
			{
				// todo return errors
				return ex.Message;
			}
			return null;
		}

		public string Listen(int handle, int backlog)
		{
			var socket = GetSocket(handle);
			if (socket == null)
				return "socketnotfound";
			socket.Blocking = false;
			socket.Listen(backlog);
			return null;
		}

		public (int Handle, string Error) Accept(int handle)
		{
			try
			{
				var socket = GetSocket(handle);
				if (socket == null)
					return (0, "socketnotfound");
				var newClient = socket.Accept();
				if (newClient != null)
				{
					var newHandle = _currentClientSocketHandle++;
					_sockets.Add(newHandle, newClient);
					return (newHandle, null);
				}
				return (0, null);
			}
			catch (Exception ex)
			{
				if (ex is SocketException)
				{
					var se = (SocketException)ex;
					if (se.SocketErrorCode == SocketError.WouldBlock)
					{
						// No incoming connection
						return (0, null);
					}
				}
				// todo other errors
				return (0, ex.Message);
			}
		}

		public (string Data, string Error) Receive(int handle, int length)
		{
			var socket = GetSocket(handle);
			if (socket == null)
				return (null, "socketnotfound");

			var buffer = new byte[length];
			try
			{
				socket.Receive(buffer, length, SocketFlags.None);

				return (Encoding.UTF8.GetString(buffer, 0, length), null);
			}
			catch (Exception ex)
			{
				if (ex is SocketException)
				{
					var se = (SocketException)ex;
					if (se.SocketErrorCode == SocketError.WouldBlock)
					{
						// no data available, move on
						return (null, null);
					}
				}
				// todo other errors
				return (null, ex.Message);
			}
		}

		public (int SentBytes, string Error) Send(int handle, string sendString)
		{
			var socket = GetSocket(handle);
			if (socket == null)
				return (0, "socketnotfound");

			try
			{
				var payloadBytes = Encoding.UTF8.GetBytes(sendString);
				var sentBytes = socket.Send(payloadBytes, SocketFlags.None);
				return (sentBytes, null);
			}
			catch (Exception ex)
			{
				// todo errors
				return (0, ex.Message);
			}
		}

		public string SetTimeout(int handle, int timeout)
		{
			var socket = GetSocket(handle);
			if (socket == null)
				return "socketnotfound";
			socket.ReceiveTimeout = timeout;
			return null;
		}
	}
}
