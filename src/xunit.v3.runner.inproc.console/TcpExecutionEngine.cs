using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Internal;
using Xunit.Runner.Common;
using Xunit.Sdk;

namespace Xunit.v3
{
	/// <summary>
	/// The execution-side engine used to host an xUnit.net v3 test assembly that communicates via
	/// TCP to the remote side, which is running <see cref="TcpRunnerEngine"/>. After connecting to
	/// the TCP port, responds to commands from the runner engine, which translate/ to commands on
	/// the <see cref="_ITestFrameworkDiscoverer"/> and <see cref="_ITestFrameworkExecutor"/>.
	/// </summary>
	public class TcpExecutionEngine : IAsyncDisposable
	{
		readonly BufferedTcpClient bufferedClient;
		readonly HashSet<string> cancellationRequested = new();
		readonly List<(byte[] command, Action<ReadOnlyMemory<byte>?> handler)> commandHandlers = new();
		readonly _IMessageSink diagnosticMessageSink;
		readonly string engineID;
		readonly HashSet<string> executingOperations = new();
		readonly int port;
		readonly XunitProjectAssembly projectAssembly;
		readonly TaskCompletionSource<int> shutdownRequested = new();
		readonly Socket socket;

		/// <summary>
		/// Initializes a new instance of the <see cref="TcpExecutionEngine"/> class.
		/// </summary>
		/// <param name="engineID">Engine ID (used for diagnostic messages).</param>
		/// <param name="port">The TCP port to connect to (localhost is assumed).</param>
		/// <param name="project">The test project.</param>
		/// <param name="diagnosticMessageSink">The message sink to send diagnostic messages to.</param>
		public TcpExecutionEngine(
			string engineID,
			int port,
			XunitProject project,
			_IMessageSink diagnosticMessageSink)
		{
			commandHandlers.Add((TcpEngineMessages.Runner.Cancel, OnCancel));
			commandHandlers.Add((TcpEngineMessages.Runner.Find, OnFind));
			commandHandlers.Add((TcpEngineMessages.Runner.Quit, OnQuit));
			commandHandlers.Add((TcpEngineMessages.Runner.Run, OnRun));

			Guard.ArgumentNotNull(nameof(project), project);
			Guard.ArgumentValid(nameof(project), "Project must have exactly one test assembly", project.Assemblies.Count == 1);

			this.engineID = Guard.ArgumentNotNull(nameof(engineID), engineID);
			this.port = port;
			this.diagnosticMessageSink = Guard.ArgumentNotNull(nameof(diagnosticMessageSink), diagnosticMessageSink);

			projectAssembly = project.Assemblies.Single();

			socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
			bufferedClient = new BufferedTcpClient($"execution::{engineID}", socket, ProcessRequest, diagnosticMessageSink);
		}

		/// <inheritdoc/>
		public async ValueTask DisposeAsync()
		{
			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Disconnecting from tcp://localhost:{port}/" });

			await bufferedClient.DisposeAsync();

			socket.Shutdown(SocketShutdown.Receive);
			socket.Shutdown(SocketShutdown.Send);
			socket.Close();
			socket.Dispose();

			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Disconnected from tcp://localhost:{port}/" });
		}

		Task Execute(string operationID)
		{
			return Task.Run((Func<Task?>)(async () =>
			{
				executingOperations.Add(operationID);
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): RUN started for operation ID '{operationID}'" });

				try
				{
					var discoveryOptions = _TestFrameworkOptions.ForDiscovery(projectAssembly.Configuration);
					var executionOptions = _TestFrameworkOptions.ForExecution(projectAssembly.Configuration);
					var assemblyInfo = new ReflectionAssemblyInfo(projectAssembly.Assembly);

					await using var disposalTracker = new DisposalTracker();
					var testFramework = ExtensibilityPointFactory.GetTestFramework(diagnosticMessageSink, assemblyInfo);
					disposalTracker.Add(testFramework);

					var cancel = false;
					var discoverySink = new TestDiscoverySink(() => cancel);

					var testDiscoverer = testFramework.GetDiscoverer(assemblyInfo);
					testDiscoverer.Find(discoverySink, discoveryOptions);
					discoverySink.Finished.WaitOne();

					var testCasesDiscovered = discoverySink.TestCases.Count;
					var filteredTestCases = discoverySink.TestCases.Where<_ITestCase>(projectAssembly.Configuration.Filters.Filter).ToList<_ITestCase>();
					var testCasesToRun = filteredTestCases.Count;

					// Run the filtered tests
					if (testCasesToRun != 0)
					{
						var callbackSink = new EngineExecutionSink(this, operationID);
						IExecutionSink resultsSink = new DelegatingExecutionSummarySink(callbackSink, () => cancel);
						var longRunningSeconds = projectAssembly.Configuration.LongRunningTestSecondsOrDefault;
						if (longRunningSeconds > 0)
							resultsSink = new DelegatingLongRunningTestDetectionSink(resultsSink, TimeSpan.FromSeconds(longRunningSeconds), diagnosticMessageSink);
						if (projectAssembly.Configuration.FailSkipsOrDefault)
							resultsSink = new DelegatingFailSkipSink(resultsSink);

						using (resultsSink)
						{
							var executor = testFramework.GetExecutor(assemblyInfo);
							executor.RunTests(filteredTestCases, resultsSink, executionOptions);
							resultsSink.Finished.WaitOne();

							if (projectAssembly.Configuration.StopOnFailOrDefault && resultsSink.ExecutionSummary.Failed != 0)
							{
								diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Cancelling all operations because operation ID '{operationID}' ended in failure" });
								foreach (var executingOperation in executingOperations)
									cancellationRequested.Add(executingOperation);
							}
						}
					}
				}
				catch (Exception ex)
				{
					var errorMessage = _ErrorMessage.FromException(ex);
					SendMessage(operationID, errorMessage);
				}

				executingOperations.Remove(operationID);
				cancellationRequested.Remove(operationID);
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): RUN finished for operation ID '{operationID}'" });
			}));
		}

		Task Find(string operationID)
		{
			return Task.Run(async () =>
			{
				executingOperations.Add(operationID);
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): FIND started for operation ID '{operationID}'" });

				try
				{
					var discoveryOptions = _TestFrameworkOptions.ForDiscovery(projectAssembly.Configuration);
					var executionOptions = _TestFrameworkOptions.ForExecution(projectAssembly.Configuration);
					var assemblyInfo = new ReflectionAssemblyInfo(projectAssembly.Assembly);

					await using var disposalTracker = new DisposalTracker();
					var testFramework = ExtensibilityPointFactory.GetTestFramework(diagnosticMessageSink, assemblyInfo);
					disposalTracker.Add(testFramework);

					var testDiscoverer = testFramework.GetDiscoverer(assemblyInfo);
					var discoverySink = new EngineDiscoverySink(this, operationID, projectAssembly.Configuration.Filters.Filter);
					testDiscoverer.Find(discoverySink, discoveryOptions);
					discoverySink.Finished.WaitOne();
				}
				catch (Exception ex)
				{
					var errorMessage = _ErrorMessage.FromException(ex);
					SendMessage(operationID, errorMessage);
				}

				executingOperations.Remove(operationID);
				cancellationRequested.Remove(operationID);
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): FIND finished for operation ID '{operationID}'" });
			});
		}

		void OnCancel(ReadOnlyMemory<byte>? data)
		{
			if (!data.HasValue || data.Value.Length == 0)
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): CANCEL data is missing the operation ID" });
				return;
			}

			var operationID = Encoding.UTF8.GetString(data.Value.ToArray());
			if (!executingOperations.Contains(operationID))
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): CANCEL requested for unknown operation ID '{operationID}'" });
				return;
			}

			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): CANCEL request recorded for operation ID '{operationID}'" });
			cancellationRequested.Add(operationID);
		}

		void OnFind(ReadOnlyMemory<byte>? data)
		{
			if (!data.HasValue || data.Value.Length == 0)
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): FIND data is missing the operation ID" });
				return;
			}

			var operationID = Encoding.UTF8.GetString(data.Value.ToArray());
			if (executingOperations.Contains(operationID))
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): FIND requested for duplicate operation ID '{operationID}'" });
				return;
			}

			_ = Find(operationID);
		}

		void OnQuit(ReadOnlyMemory<byte>? _) =>
			shutdownRequested.TrySetResult(0);

		void OnRun(ReadOnlyMemory<byte>? data)
		{
			if (!data.HasValue || data.Value.Length == 0)
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): RUN data is missing the operation ID" });
				return;
			}

			var operationID = Encoding.UTF8.GetString(data.Value.ToArray());
			if (executingOperations.Contains(operationID))
			{
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): RUN requested for duplicate operation ID '{operationID}'" });
				return;
			}

			_ = Execute(operationID);
		}

		void ProcessRequest(ReadOnlyMemory<byte> request)
		{
			var (command, data) = TcpEngineMessages.SplitOnSeparator(request);

			foreach (var commandHandler in commandHandlers)
				if (command.Span.SequenceEqual(commandHandler.command))
				{
					commandHandler.handler(data);
					return;
				}

			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Received unknown command: '{Encoding.UTF8.GetString(command.ToArray())}'" });
		}

		/// <summary>
		/// Sends <see cref="TcpEngineMessages.Execution.Message"/>.
		/// </summary>
		/// <param name="operationID">The operation ID that this message is for.</param>
		/// <param name="message">The message to be sent.</param>
		/// <returns>Returns <c>true</c> if the operation should continue to run tests; <c>false</c> if it should cancel the run.</returns>
		public bool SendMessage(
			string operationID,
			_MessageSinkMessage message)
		{
			Guard.ArgumentNotNull(nameof(operationID), operationID);
			Guard.ArgumentNotNull(nameof(message), message);

			bufferedClient.Send(TcpEngineMessages.Execution.Message);
			bufferedClient.Send(TcpEngineMessages.Separator);
			bufferedClient.Send(operationID);
			bufferedClient.Send(TcpEngineMessages.Separator);
			bufferedClient.Send(message.ToJson());
			bufferedClient.Send(TcpEngineMessages.EndOfMessage);

			return !cancellationRequested.Contains(operationID);
		}

		/// <summary>
		/// Starts the execution engine, connecting back to the runner engine on the TCP port
		/// provided to the constructor.
		/// </summary>
		/// <returns>The local port used for the conection.</returns>
		public async ValueTask<int> Start()
		{
			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Connecting to tcp://localhost:{port}/" });

			await socket.ConnectAsync(IPAddress.Loopback, port);
			bufferedClient.Start();

			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Connected to tcp://localhost:{port}/" });

			return ((IPEndPoint?)socket.LocalEndPoint)?.Port ?? throw new InvalidOperationException("Could not retrieve port from socket local endpoint");
		}

		/// <summary>
		/// Waits for the QUIT signal from the runner engine.
		/// </summary>
		// TODO: CancellationToken? Timespan for timeout?
		public Task WaitForQuit() =>
			shutdownRequested.Task;

		class EngineDiscoverySink : _IMessageSink
		{
			readonly TcpExecutionEngine engine;
			readonly Func<_ITestCase, bool> filter;
			readonly string operationID;

			public EngineDiscoverySink(
				TcpExecutionEngine engine,
				string operationID,
				Func<_ITestCase, bool> filter)
			{
				this.engine = engine;
				this.operationID = operationID;
				this.filter = filter;
			}

			public ManualResetEvent Finished { get; } = new(initialState: false);

			public bool OnMessage(_MessageSinkMessage message)
			{
				var sendMessage = true;

				if (message is _TestCaseDiscovered discovered)
					sendMessage = filter(discovered.TestCase);

				if (message is _DiscoveryComplete)
					Finished.Set();

				if (sendMessage)
					engine.SendMessage(operationID, message);

				return !engine.cancellationRequested.Contains(operationID);
			}
		}

		class EngineExecutionSink : _IMessageSink
		{
			readonly TcpExecutionEngine engine;
			readonly string operationID;

			public EngineExecutionSink(
				TcpExecutionEngine engine,
				string operationID)
			{
				this.engine = engine;
				this.operationID = operationID;
			}

			public bool OnMessage(_MessageSinkMessage message)
			{
				engine.SendMessage(operationID, message);
				return !engine.cancellationRequested.Contains(operationID);
			}
		}
	}
}
