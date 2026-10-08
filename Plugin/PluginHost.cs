using System;
using System.Threading;
using System.Threading.Tasks;
using NuGetPluginKeyring.Logging;
using NuGet.Protocol.Plugins;

namespace NuGetPluginKeyring.Plugin
{
    /// <summary>
    /// Runs the process as a NuGet plugin: performs the handshake over stdin/stdout, serves
    /// requests, and shuts down when NuGet closes the connection.
    /// </summary>
    internal static class PluginHost
    {
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMinutes(2);

        public static async Task<int> RunAsync(
            CredentialResolver resolver,
            MultiProviderLogger logger,
            CancellationToken cancellationToken)
        {
            var connectionLogger = new PluginConnectionLoggerPlaceholder();

            IRequestHandlers requestHandlers = new RequestHandlers();

            requestHandlers.TryAdd(
                MessageMethod.GetAuthenticationCredentials,
                new GetAuthenticationCredentialsRequestHandler(logger, resolver, cancellationToken));
            requestHandlers.TryAdd(
                MessageMethod.GetOperationClaims,
                new GetOperationClaimsRequestHandler(logger));
            requestHandlers.TryAdd(
                MessageMethod.Initialize,
                new InitializeRequestHandler(logger));
            requestHandlers.TryAdd(
                MessageMethod.SetLogLevel,
                new SetLogLevelRequestHandler(logger, connectionLogger));
            requestHandlers.TryAdd(
                MessageMethod.SetCredentials,
                new SetCredentialsRequestHandler(logger));

            try
            {
                using IPlugin plugin = await PluginFactory.CreateFromCurrentProcessAsync(
                    requestHandlers,
                    ConnectionOptions.CreateDefault(),
                    cancellationToken).ConfigureAwait(false);

                // Only now is there a connection to log over. The placeholder forwards to it and
                // stays at Error level until NuGet sends SetLogLevel.
                var realLogger = new PluginConnectionLogger(plugin.Connection);
                connectionLogger.Attach(realLogger);
                logger.Add(connectionLogger);

                logger.Verbose(
                    $"Keyring credential provider running as plugin '{plugin.Name}' "
                    + $"(protocol {plugin.Connection.ProtocolVersion}).");

                await WaitForCloseAsync(plugin, logger).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                // When restoring from several sources NuGet cancels the losing requests; this is
                // noise rather than a failure.
                logger.Verbose($"Plugin session cancelled: {exception.Message}");
            }

            return 0;
        }

        private static async Task WaitForCloseAsync(IPlugin plugin, IProviderLogger logger)
        {
            var closing = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            plugin.Connection.Faulted += (_, args) =>
            {
                logger.Error(
                    "Plugin connection faulted on "
                    + $"{args.Message?.Type} {args.Message?.Method} {args.Message?.RequestId}: "
                    + args.Exception);
            };

            plugin.BeforeClose += (_, _) => closing.TrySetResult(null);

            plugin.Closed += (_, _) =>
            {
                closing.TrySetResult(null);
                closed.TrySetResult(null);
            };

            await closing.Task.ConfigureAwait(false);

            using var timer = new Timer(
                _ => closed.TrySetCanceled(), null, ShutdownTimeout, Timeout.InfiniteTimeSpan);

            try
            {
                await closed.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.Error(
                    $"Plugin did not finish shutting down within {ShutdownTimeout}; exiting anyway.");
            }
        }
    }

    /// <summary>
    /// Stands in for the connection logger so that handlers can be constructed before the
    /// connection exists. <see cref="MinimumLevel"/> is tracked here and pushed into the real
    /// logger on attach, so a <c>SetLogLevel</c> that arrives first is not lost.
    /// </summary>
    internal sealed class PluginConnectionLoggerPlaceholder : IProviderLogger
    {
        private IProviderLogger? _inner;
        private NuGet.Common.LogLevel _minimumLevel = NuGet.Common.LogLevel.Error;

        public NuGet.Common.LogLevel MinimumLevel
        {
            get => _inner?.MinimumLevel ?? _minimumLevel;
            set
            {
                _minimumLevel = value;

                if (_inner is not null)
                {
                    _inner.MinimumLevel = value;
                }
            }
        }

        public void Attach(IProviderLogger inner)
        {
            inner.MinimumLevel = _minimumLevel;
            _inner = inner;
        }

        public void Log(NuGet.Common.LogLevel level, string message)
            => _inner?.Log(level, message);
    }
}
