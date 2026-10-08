using System;
using System.Threading;
using System.Threading.Tasks;
using NuGetPluginKeyring.Logging;
using NuGet.Protocol.Plugins;

namespace NuGetPluginKeyring.Plugin
{
    /// <summary>
    /// Answers NuGet's credential request from the keyring.
    /// <para>
    /// A keyring read is non-interactive — it neither prompts nor blocks on user input — so this
    /// handler serves requests regardless of <c>IsNonInteractive</c> and <c>CanShowDialog</c>.
    /// That is the whole point: <c>dotnet restore</c> works without <c>--interactive</c>.
    /// </para>
    /// </summary>
    internal sealed class GetAuthenticationCredentialsRequestHandler
        : RequestHandlerBase<GetAuthenticationCredentialsRequest, GetAuthenticationCredentialsResponse>
    {
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

        private readonly CredentialResolver _resolver;
        private readonly CancellationToken _sessionCancellationToken;

        public GetAuthenticationCredentialsRequestHandler(
            IProviderLogger logger,
            CredentialResolver resolver,
            CancellationToken sessionCancellationToken)
            : base(logger)
        {
            _resolver = resolver;
            _sessionCancellationToken = sessionCancellationToken;
        }

        public override CancellationToken CancellationToken => _sessionCancellationToken;

        protected override async Task<GetAuthenticationCredentialsResponse> HandleRequestAsync(
            GetAuthenticationCredentialsRequest request, CancellationToken cancellationToken)
        {
            if (request.Uri is null)
            {
                return Respond(MessageResponseCode.Error, "The request contained no URI.");
            }

            Logger.Verbose(
                $"Credential request for '{request.Uri.AbsoluteUri}' "
                + $"(isRetry: {request.IsRetry}, isNonInteractive: {request.IsNonInteractive}, "
                + $"canShowDialog: {request.CanShowDialog}).");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _sessionCancellationToken);

            CredentialResult result = await _resolver
                .ResolveAsync(request.Uri, request.IsRetry, linked.Token)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case CredentialOutcome.Success:
                    return new GetAuthenticationCredentialsResponse(
                        username: result.Username,
                        password: result.Password,
                        message: null,
                        authenticationTypes: result.AuthenticationTypes,
                        responseCode: MessageResponseCode.Success);

                case CredentialOutcome.NotFound:
                    // NotFound lets NuGet fall through to other credential sources.
                    return Respond(MessageResponseCode.NotFound, result.Message);

                default:
                    return Respond(MessageResponseCode.Error, result.Message);
            }
        }

        private static GetAuthenticationCredentialsResponse Respond(
            MessageResponseCode responseCode, string? message)
            => new GetAuthenticationCredentialsResponse(
                username: null,
                password: null,
                message: message,
                authenticationTypes: null,
                responseCode: responseCode);

        protected override GetAuthenticationCredentialsResponse CreateErrorResponse(
            Exception exception)
            => Respond(MessageResponseCode.Error, exception.Message);

        /// <summary>
        /// Launching a process and waiting on the wallet can exceed the 5 second request timeout,
        /// so keep NuGet informed that we are still working.
        /// </summary>
        protected override AutomaticProgressReporter? GetProgressReporter(
            IConnection connection, Message request, CancellationToken cancellationToken)
            => AutomaticProgressReporter.Create(
                connection, request, ProgressInterval, cancellationToken);
    }
}
