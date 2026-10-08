using System;
using System.Threading;
using System.Threading.Tasks;
using KeyringNuGetCredProvider.Logging;
using NuGet.Protocol.Plugins;

namespace KeyringNuGetCredProvider.Plugin
{
    /// <summary>
    /// Deserializes the request payload, runs the handler, and sends the response.
    /// </summary>
    internal abstract class RequestHandlerBase<TRequest, TResponse> : IRequestHandler
        where TRequest : class
        where TResponse : class
    {
        protected RequestHandlerBase(IProviderLogger logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public virtual CancellationToken CancellationToken => CancellationToken.None;

        protected IProviderLogger Logger { get; }

        public async Task HandleResponseAsync(
            IConnection connection,
            Message request,
            IResponseHandler responseHandler,
            CancellationToken cancellationToken)
        {
            TRequest? payload = MessageUtilities.DeserializePayload<TRequest>(request);

            if (payload is null)
            {
                Logger.Error(
                    $"{request.Method} request {request.RequestId} carried no payload.");

                TResponse? empty = CreateErrorResponse(
                    new InvalidOperationException("The request carried no payload."));

                if (empty is null)
                {
                    throw new InvalidOperationException(
                        $"{request.Method} request {request.RequestId} carried no payload.");
                }

                await responseHandler
                    .SendResponseAsync(request, empty, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            TResponse? response;

            try
            {
                using (GetProgressReporter(connection, request, cancellationToken))
                {
                    response = await HandleRequestAsync(payload, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // NuGet cancelled the request; it is not expecting a response any more.
                Logger.Verbose($"{request.Method} request {request.RequestId} was cancelled.");
                return;
            }
            catch (Exception exception)
            {
                Logger.Error(
                    $"Unhandled error while handling {request.Method} request "
                    + $"{request.RequestId}: {exception}");

                response = CreateErrorResponse(exception);

                if (response is null)
                {
                    throw;
                }
            }

            // Once we have a response NuGet must receive it, even if the operation was cancelled
            // in the meantime; otherwise the client waits for the full request timeout.
            await responseHandler
                .SendResponseAsync(request, response, CancellationToken.None)
                .ConfigureAwait(false);
        }

        protected abstract Task<TResponse> HandleRequestAsync(
            TRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Lets a handler answer with a protocol-level error instead of faulting the connection.
        /// </summary>
        protected virtual TResponse? CreateErrorResponse(Exception exception) => null;

        /// <summary>
        /// Requests time out after 5 seconds unless the plugin reports progress. Handlers that
        /// can take longer (a keyring query) override this.
        /// </summary>
        protected virtual AutomaticProgressReporter? GetProgressReporter(
            IConnection connection, Message request, CancellationToken cancellationToken)
            => null;
    }
}
