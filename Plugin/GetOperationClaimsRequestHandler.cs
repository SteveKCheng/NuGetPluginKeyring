using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGetPluginKeyring.Logging;
using NuGet.Protocol.Plugins;

namespace NuGetPluginKeyring.Plugin
{
    /// <summary>
    /// Declares what this plugin can do.
    /// <para>
    /// Under protocol 2.0.0 NuGet asks twice: once with a null package source and service index,
    /// which is the source-agnostic question "are you an authentication provider?", and once per
    /// source for download-related claims. We answer <see cref="OperationClaim.Authentication"/>
    /// to the first and an empty set to the second — claiming
    /// <see cref="OperationClaim.DownloadPackage"/> would make NuGet route package downloads
    /// through us.
    /// </para>
    /// </summary>
    internal sealed class GetOperationClaimsRequestHandler
        : RequestHandlerBase<GetOperationClaimsRequest, GetOperationClaimsResponse>
    {
        private static readonly GetOperationClaimsResponse AuthenticationOnly =
            new GetOperationClaimsResponse(new[] { OperationClaim.Authentication });

        private static readonly GetOperationClaimsResponse None =
            new GetOperationClaimsResponse(Array.Empty<OperationClaim>());

        public GetOperationClaimsRequestHandler(IProviderLogger logger) : base(logger) { }

        protected override Task<GetOperationClaimsResponse> HandleRequestAsync(
            GetOperationClaimsRequest request, CancellationToken cancellationToken)
        {
            // ServiceIndex is obsolete in NuGet.Protocol 7.x and always returns null, so the
            // source-agnostic request is identified through ServiceIndexJson.
            bool sourceAgnostic =
                string.IsNullOrEmpty(request.PackageSourceRepository)
                && string.IsNullOrEmpty(request.ServiceIndexJson);

            if (sourceAgnostic)
            {
                Logger.Verbose("Claiming the Authentication operation.");
                return Task.FromResult(AuthenticationOnly);
            }

            Logger.Verbose(
                $"Claiming no operations for source '{request.PackageSourceRepository}'.");

            return Task.FromResult(None);
        }

        protected override GetOperationClaimsResponse CreateErrorResponse(Exception exception)
            => None;
    }
}
