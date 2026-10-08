using System;
using System.Threading;
using System.Threading.Tasks;
using NuGetPluginKeyring.Logging;
using NuGet.Protocol.Plugins;

namespace NuGetPluginKeyring.Plugin
{
    /// <summary>
    /// Accepts NuGet's <c>Initialize</c> message and adopts the client's request timeout.
    /// </summary>
    internal sealed class InitializeRequestHandler
        : RequestHandlerBase<InitializeRequest, InitializeResponse>
    {
        public InitializeRequestHandler(IProviderLogger logger) : base(logger) { }

        protected override Task<InitializeResponse> HandleRequestAsync(
            InitializeRequest request, CancellationToken cancellationToken)
        {
            Logger.Verbose(
                $"Initialized by NuGet {request.ClientVersion} (culture {request.Culture}, "
                + $"request timeout {request.RequestTimeout}).");

            return Task.FromResult(new InitializeResponse(MessageResponseCode.Success));
        }

        protected override InitializeResponse CreateErrorResponse(Exception exception)
            => new InitializeResponse(MessageResponseCode.Error);
    }

    /// <summary>
    /// Applies the log level NuGet asks for. Until this arrives, nothing below
    /// <see cref="NuGet.Common.LogLevel.Error"/> may be sent over the connection.
    /// </summary>
    internal sealed class SetLogLevelRequestHandler
        : RequestHandlerBase<SetLogLevelRequest, SetLogLevelResponse>
    {
        private readonly IProviderLogger _connectionLogger;

        public SetLogLevelRequestHandler(IProviderLogger logger, IProviderLogger connectionLogger)
            : base(logger)
        {
            _connectionLogger = connectionLogger;
        }

        protected override Task<SetLogLevelResponse> HandleRequestAsync(
            SetLogLevelRequest request, CancellationToken cancellationToken)
        {
            _connectionLogger.MinimumLevel = request.LogLevel;
            Logger.Verbose($"NuGet set the plugin log level to {request.LogLevel}.");

            return Task.FromResult(new SetLogLevelResponse(MessageResponseCode.Success));
        }

        protected override SetLogLevelResponse CreateErrorResponse(Exception exception)
            => new SetLogLevelResponse(MessageResponseCode.Error);
    }

    /// <summary>
    /// NuGet pushes the credentials it already knows (from <c>NuGet.Config</c>, and proxy
    /// credentials) before asking for new ones. We source everything from the keyring, so this
    /// is acknowledged and only logged.
    /// </summary>
    internal sealed class SetCredentialsRequestHandler
        : RequestHandlerBase<SetCredentialsRequest, SetCredentialsResponse>
    {
        public SetCredentialsRequestHandler(IProviderLogger logger) : base(logger) { }

        protected override Task<SetCredentialsResponse> HandleRequestAsync(
            SetCredentialsRequest request, CancellationToken cancellationToken)
        {
            Logger.Verbose(
                $"NuGet supplied known credentials for '{request.PackageSourceRepository}' "
                + $"(username: {Describe(request.Username)}, "
                + $"password: {Describe(request.Password)}, "
                + $"proxy username: {Describe(request.ProxyUsername)}, "
                + $"proxy password: {Describe(request.ProxyPassword)}).");

            return Task.FromResult(new SetCredentialsResponse(MessageResponseCode.Success));
        }

        private static string Describe(string? value)
            => string.IsNullOrEmpty(value) ? "<none>" : "<set>";

        protected override SetCredentialsResponse CreateErrorResponse(Exception exception)
            => new SetCredentialsResponse(MessageResponseCode.Error);
    }
}
