using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KeyringNuGetCredProvider.Configuration;
using KeyringNuGetCredProvider.Keyring;
using KeyringNuGetCredProvider.Logging;

namespace KeyringNuGetCredProvider
{
    internal enum CredentialOutcome
    {
        /// <summary>Credentials were resolved.</summary>
        Success,

        /// <summary>This provider is not configured for the URI, or the entry is unknown.</summary>
        NotFound,

        /// <summary>Configured, but the lookup itself broke.</summary>
        Error,
    }

    internal sealed class CredentialResult
    {
        private CredentialResult(
            CredentialOutcome outcome,
            string? username,
            string? password,
            IList<string>? authenticationTypes,
            string? message)
        {
            Outcome = outcome;
            Username = username;
            Password = password;
            AuthenticationTypes = authenticationTypes;
            Message = message;
        }

        public CredentialOutcome Outcome { get; }

        public string? Username { get; }

        public string? Password { get; }

        public IList<string>? AuthenticationTypes { get; }

        /// <summary>Diagnostic text surfaced to NuGet. Never contains the password.</summary>
        public string? Message { get; }

        public static CredentialResult Success(
            string username, string password, IList<string> authenticationTypes)
            => new CredentialResult(
                CredentialOutcome.Success, username, password, authenticationTypes, null);

        public static CredentialResult NotFound(string message)
            => new CredentialResult(CredentialOutcome.NotFound, null, null, null, message);

        public static CredentialResult Error(string message)
            => new CredentialResult(CredentialOutcome.Error, null, null, null, message);
    }

    /// <summary>
    /// Turns a credential request for a URI into a username/password pair by looking the
    /// password up in the keyring.
    /// </summary>
    internal sealed class CredentialResolver
    {
        private static readonly string[] DefaultAuthenticationTypes = { "basic" };

        private readonly ProviderConfiguration _configuration;
        private readonly KeyringReader _reader;
        private readonly IProviderLogger _logger;

        /// <summary>
        /// A problem found at startup, before there was a connection to report it over.
        /// Emitted on first use so that it reaches the user through NuGet's output.
        /// </summary>
        private readonly string? _startupWarning;

        private int _startupWarningEmitted;

        public CredentialResolver(
            ProviderConfiguration configuration,
            KeyringReader reader,
            IProviderLogger logger,
            string? startupWarning = null)
        {
            _configuration = configuration;
            _reader = reader;
            _logger = logger;
            _startupWarning = startupWarning;
        }

        /// <summary>
        /// Reports a deferred startup problem exactly once. A broken configuration is surfaced as
        /// a warning rather than an error: the restore may well involve sources this provider is
        /// not responsible for, and those must keep working.
        /// </summary>
        private void EmitStartupWarningOnce()
        {
            if (_startupWarning is null)
            {
                return;
            }

            if (Interlocked.Exchange(ref _startupWarningEmitted, 1) != 0)
            {
                return;
            }

            _logger.Warning($"Keyring credential provider: {_startupWarning}");
        }

        /// <summary>
        /// Resolves the command line that would be used for <paramref name="uri"/> without
        /// executing it. Returns <c>null</c> when no source matches.
        /// </summary>
        public KeyringCommand? DescribeCommand(Uri uri, out SourceConfiguration? source)
        {
            source = _configuration.FindSource(uri);

            if (source is null)
            {
                return null;
            }

            return _reader.BuildCommand(source, uri, ResolveUsername(source));
        }

        public async Task<CredentialResult> ResolveAsync(
            Uri uri,
            bool isRetry,
            CancellationToken cancellationToken)
        {
            EmitStartupWarningOnce();

            SourceConfiguration? source = _configuration.FindSource(uri);

            if (source is null)
            {
                string message =
                    $"No configured source matches '{uri.AbsoluteUri}'"
                    + (_configuration.FilePath is null
                        ? " (no configuration file found; see README)."
                        : $" in '{_configuration.FilePath}'.");

                _logger.Verbose(message);
                return CredentialResult.NotFound(message);
            }

            _logger.Verbose(
                $"'{uri.AbsoluteUri}' matched source '{source.DisplayName}' "
                + $"-> keyring entry '{source.Entry}'.");

            if (isRetry)
            {
                // NuGet only retries after the credentials we supplied were rejected. We hold no
                // cache to invalidate, so re-reading returns the same value; say so loudly rather
                // than letting the user wonder why the restore keeps failing.
                _logger.Warning(
                    $"The credentials from keyring entry '{source.Entry}' were rejected by "
                    + $"'{uri.Host}'. Re-reading the entry; if this repeats, the stored secret is "
                    + "probably stale or the username is wrong.");
            }

            string? username = ResolveUsername(source);

            if (string.IsNullOrEmpty(username))
            {
                string message =
                    $"Source '{source.DisplayName}' has no username. Set \"username\" on the "
                    + "source or \"defaultUsername\" at the top level of the configuration file.";

                _logger.Error(message);
                return CredentialResult.Error(message);
            }

            KeyringCommand command;

            try
            {
                command = _reader.BuildCommand(source, uri, username);
            }
            catch (ConfigurationException exception)
            {
                _logger.Error(exception.Message);
                return CredentialResult.Error(exception.Message);
            }

            KeyringLookupResult lookup =
                await _reader.ReadAsync(command, cancellationToken).ConfigureAwait(false);

            switch (lookup.Status)
            {
                case KeyringLookupStatus.Found:
                    _logger.Verbose(
                        $"Supplying credentials for '{uri.Host}' as user '{username}'.");

                    return CredentialResult.Success(
                        username,
                        lookup.Secret!,
                        source.AuthenticationTypes is { Count: > 0 }
                            ? source.AuthenticationTypes
                            : new List<string>(DefaultAuthenticationTypes));

                case KeyringLookupStatus.NotFound:
                    _logger.Warning(
                        $"Keyring entry '{source.Entry}' holds no value. {lookup.Diagnostics}");

                    return CredentialResult.NotFound(lookup.Diagnostics ?? "Entry not found.");

                default:
                    _logger.Error(
                        $"Keyring lookup for entry '{source.Entry}' failed. {lookup.Diagnostics}");

                    return CredentialResult.Error(lookup.Diagnostics ?? "Keyring lookup failed.");
            }
        }

        private string? ResolveUsername(SourceConfiguration source)
            => string.IsNullOrEmpty(source.Username)
                ? _configuration.DefaultUsername
                : source.Username;
    }
}
