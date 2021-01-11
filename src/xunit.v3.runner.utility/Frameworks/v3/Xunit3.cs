//using System;
//using System.Collections.Generic;
//using System.Threading.Tasks;
//using Xunit.Internal;
//using Xunit.Sdk;

//namespace Xunit.v3
//{
//	/// <summary>
//	/// This class be used to do discovery and execution of xUnit.net v3 tests.
//	/// Runner authors are strongly encouraged to use <see cref="XunitFrontController"/>
//	/// instead of using this class directly.
//	/// </summary>
//	public class Xunit3 : IFrontController, _IMessageSink, IAsyncDisposable
//	{
//		readonly string assemblyFileName;
//		readonly string? configFileName;
//		_IMessageSink? currentMessageSink;
//		readonly _IMessageSink diagnosticMessageSink;
//		readonly DisposalTracker disposalTracker = new DisposalTracker();
//		readonly _ISourceInformationProvider sourceInformationProvider;
//		readonly TcpRunnerEngine tcpServer;

//		/// <summary>
//		/// Initializes a new instance of the <see cref="Xunit3"/> class.
//		/// </summary>
//		/// <param name="diagnosticMessageSink">The message sink which receives <see cref="_DiagnosticMessage"/> messages.</param>
//		/// <param name="sourceInformationProvider">Source code information provider.</param>
//		/// <param name="assemblyFileName">The test assembly.</param>
//		/// <param name="configFileName">The test assembly configuration file.</param>
//		public Xunit3(
//			_IMessageSink diagnosticMessageSink,
//			_ISourceInformationProvider sourceInformationProvider,
//			string assemblyFileName,
//			string? configFileName = null)
//		{
//			this.diagnosticMessageSink = Guard.ArgumentNotNull(nameof(diagnosticMessageSink), diagnosticMessageSink);
//			this.sourceInformationProvider = Guard.ArgumentNotNull(nameof(sourceInformationProvider), sourceInformationProvider);
//			this.assemblyFileName = Guard.ArgumentNotNullOrEmpty(nameof(assemblyFileName), assemblyFileName);
//			this.configFileName = configFileName;

//			tcpServer = new TcpRunnerEngine(this, diagnosticMessageSink);
//			disposalTracker.Add(tcpServer);

//			var port = tcpServer.Start();
//			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"v3 TCP Server running on tcp://localhost:{port}/ for '{assemblyFileName}'" });

//			// TODO: Launch the unit test assembly
//		}

//		/// <inheritdoc/>
//		public bool CanUseAppDomains => false;

//		/// <inheritdoc/>
//		public string TargetFramework => throw new NotImplementedException();

//		/// <inheritdoc/>
//		public string TestAssemblyUniqueID => throw new NotImplementedException();

//		/// <inheritdoc/>
//		public string TestFrameworkDisplayName => throw new NotImplementedException();

//		/// <inheritdoc/>
//		public ValueTask DisposeAsync() =>
//			disposalTracker.DisposeAsync();

//		/// <inheritdoc/>
//		public void Find(
//			_IMessageSink discoveryMessageSink,
//			_ITestFrameworkDiscoveryOptions discoveryOptions)
//		{
//			//var cmd = $"DISA 1 {discoveryOptions.Serialize()}";
//			throw new NotImplementedException();
//		}

//		/// <inheritdoc/>
//		public void Find(string typeName,
//			_IMessageSink discoveryMessageSink,
//			_ITestFrameworkDiscoveryOptions discoveryOptions)
//		{
//			throw new NotImplementedException();
//		}

//		bool _IMessageSink.OnMessage(_MessageSinkMessage message) =>
//			currentMessageSink?.OnMessage(message) ?? true;

//		/// <inheritdoc/>
//		public void RunAll(
//			_IMessageSink executionMessageSink,
//			_ITestFrameworkDiscoveryOptions discoveryOptions,
//			_ITestFrameworkExecutionOptions executionOptions)
//		{
//			throw new NotImplementedException();
//		}

//		/// <inheritdoc/>
//		public void RunTests(
//			IEnumerable<_ITestCase> testCases,
//			_IMessageSink executionMessageSink,
//			_ITestFrameworkExecutionOptions executionOptions)
//		{
//			throw new NotImplementedException();
//		}

//		/// <inheritdoc/>
//		public void RunTests(
//			IEnumerable<string> serializedTestCases,
//			_IMessageSink executionMessageSink,
//			_ITestFrameworkExecutionOptions executionOptions)
//		{
//			throw new NotImplementedException();
//		}
//	}
//}
