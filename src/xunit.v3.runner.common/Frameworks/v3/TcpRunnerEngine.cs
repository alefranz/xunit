using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Internal;

namespace Xunit.v3
{
	/// <summary>
	/// The runner-side engine used to host an xUnit.net v3 test assembly. Opens a port
	/// for message communication, and translates the communication channel back into v3
	/// message objects which are passed to the provided <see cref="_IMessageSink"/>.
	/// Sends commands to the remote side, which is running <see cref="T:Xunit.v3.TcpExecutionEngine"/>.
	/// </summary>
	public class TcpRunnerEngine : IAsyncDisposable
	{
		int cancelRequested = 0;
		BufferedTcpClient? bufferedClient;
		readonly List<Action> cleanupTasks = new();
		readonly List<(byte[] command, Action<ReadOnlyMemory<byte>?> handler)> commandHandlers = new();
		readonly _IMessageSink diagnosticMessageSink;
		readonly string engineID;
		readonly _IMessageSink messageSink;
		bool quitSent = false;

		/// <summary>
		/// Initializes a new instance of the <see cref="TcpRunnerEngine"/> class.
		/// </summary>
		/// <param name="engineID">Engine ID (used for diagnostic messages).</param>
		/// <param name="messageSink">The message sink to pass remote messages to.</param>
		/// <param name="diagnosticMessageSink">The message sink to send diagnostic messages to.</param>
		public TcpRunnerEngine(
			string engineID,
			_IMessageSink messageSink,
			_IMessageSink diagnosticMessageSink)
		{
			commandHandlers.Add((TcpEngineMessages.Execution.Message, OnMessage));

			this.engineID = Guard.ArgumentNotNull(nameof(engineID), engineID);
			this.messageSink = Guard.ArgumentNotNull(nameof(messageSink), messageSink);
			this.diagnosticMessageSink = Guard.ArgumentNotNull(nameof(diagnosticMessageSink), diagnosticMessageSink);
		}

		/// <summary>
		/// Gets a flag which indicates whether an execution engine is connected.
		/// </summary>
		public bool Connected =>
			bufferedClient != null;

		/// <inheritdoc/>
		public async ValueTask DisposeAsync()
		{
			if (bufferedClient != null)
			{
				if (!quitSent)
					SendQuit();

				await bufferedClient.DisposeAsync();
			}

			foreach (var cleanupTask in cleanupTasks)
			{
				try
				{
					cleanupTask();
				}
				catch (Exception ex)
				{
					diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): Error during TCP server cleanup: {ex}" });
				}
			}
		}

		void OnMessage(ReadOnlyMemory<byte>? data)
		{
			if (!data.HasValue)
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): MSG data is missing the operation ID and JSON" });
				return;
			}

			var (requestID, json) = TcpEngineMessages.SplitOnSeparator(data.Value);

			if (!json.HasValue)
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): MSG data is missing the JSON" });
				return;
			}

			var deserializedMessage = _MessageSinkMessage.ParseJson(json.Value);
			var @continue = messageSink.OnMessage(deserializedMessage);

			if (!@continue && Interlocked.Exchange(ref cancelRequested, 1) == 0)
			{
				bufferedClient?.Send(TcpEngineMessages.Runner.Cancel);
				bufferedClient?.Send(TcpEngineMessages.EndOfMessage);
			}
		}

		void ProcessRequest(ReadOnlyMemory<byte> request)
		{
			var (command, data) = TcpEngineMessages.SplitOnSeparator(request);

			foreach (var commandHandler in commandHandlers)
				if (command.Span.SequenceEqual(commandHandler.command))
				{
					try
					{
						commandHandler.handler(data);
					}
					catch (Exception ex)
					{
						diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): Error during message processing '{Encoding.UTF8.GetString(request.ToArray())}': {ex}" });
					}

					return;
				}

			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): Received unknown command '{Encoding.UTF8.GetString(request.ToArray())}'" });
		}

		/// <summary>
		/// Sends <see cref="TcpEngineMessages.Runner.Cancel"/>.
		/// </summary>
		/// <param name="operationID">The operation ID to cancel.</param>
		public void SendCancel(string operationID)
		{
			Guard.ArgumentNotNull(nameof(operationID), operationID);

			if (bufferedClient == null)
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): SendCancel called when there is no connected execution engine" });
				return;
			}

			bufferedClient.Send(TcpEngineMessages.Runner.Cancel);
			bufferedClient.Send(TcpEngineMessages.Separator);
			bufferedClient.Send(operationID);
			bufferedClient.Send(TcpEngineMessages.EndOfMessage);
		}

		/// <summary>
		/// Sends <see cref="TcpEngineMessages.Runner.Quit"/>.
		/// </summary>
		public void SendQuit()
		{
			if (bufferedClient == null)
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): SendQuit called when there is no connected execution engine" });
				return;
			}

			quitSent = true;

			bufferedClient.Send(TcpEngineMessages.Runner.Quit);
			bufferedClient.Send(TcpEngineMessages.EndOfMessage);
		}

		/// <summary>
		/// Start the TCP server. Stop the server by disposing it.
		/// </summary>
		/// <returns>Returns the TCP port that the server is listening on.</returns>
		public int Start()
		{
			var listenSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
			cleanupTasks.Add(() =>
			{
				listenSocket.Close();
				listenSocket.Dispose();
			});

			listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
			listenSocket.Listen(1);

			Task.Run(async () =>
			{
				var socket = await listenSocket.AcceptAsync();
				listenSocket.Close();

				var remotePort = ((IPEndPoint?)socket.RemoteEndPoint)?.Port.ToString() ?? "<unknown_port>";

				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): Connection accepted from tcp://localhost:{remotePort}/" });

				cleanupTasks.Add(() =>
				{
					diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): Disconnecting from tcp://localhost:{remotePort}/" });

					socket.Shutdown(SocketShutdown.Receive);
					socket.Shutdown(SocketShutdown.Send);
					socket.Close();
					socket.Dispose();

					diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): Disconnected from tcp://localhost:{remotePort}/" });
				});

				bufferedClient = new BufferedTcpClient($"runner::{engineID}", socket, ProcessRequest, diagnosticMessageSink);
				bufferedClient.Start();
			});

			var port = ((IPEndPoint)listenSocket.LocalEndPoint!).Port;

			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpRunnerEngine({engineID}): Listening on tcp://localhost:{port}/" });

			return port;
		}
	}
}
