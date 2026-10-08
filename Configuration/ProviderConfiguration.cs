using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NuGetPluginKeyring.Configuration
{
    internal sealed class ConfigurationException : Exception
    {
        public ConfigurationException(string message) : base(message) { }
        public ConfigurationException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Describes how to invoke the keyring query executable.
    /// </summary>
    internal sealed class KeyringCommandConfiguration
    {
        /// <summary>Executable to launch. Resolved via PATH when not absolute.</summary>
        public string Executable { get; set; } = "kwallet-query";

        /// <summary>
        /// Argument templates, in order.  Placeholders are <c>{entry}</c>, <c>{username}</c>,
        /// <c>{folder}</c>, <c>{wallet}</c>, <c>{uri}</c>, <c>{host}</c>.
        /// </summary>
        public List<ArgumentTemplate> Arguments { get; set; } = new List<ArgumentTemplate>
        {
            // NOTE: verify these against your kwallet-query version; see README.
            new ArgumentTemplate(new[] { "--read-password", "{entry}" }),
            new ArgumentTemplate(new[] { "--folder", "{folder}" }),
            new ArgumentTemplate(new[] { "{wallet}" }),
        };

        /// <summary>Hard timeout for the lookup. The process is killed when it elapses.</summary>
        public int TimeoutSeconds { get; set; } = 10;

        /// <summary>Default wallet, used when a source does not override it.</summary>
        public string? Wallet { get; set; }

        /// <summary>Default folder, used when a source does not override it.</summary>
        public string? Folder { get; set; }

        /// <summary>
        /// Extra environment variables for the child process. Useful for things like
        /// <c>DISPLAY</c> or <c>DBUS_SESSION_BUS_ADDRESS</c> when restoring from a context
        /// that does not inherit them.
        /// </summary>
        public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Maps one NuGet source (by URI prefix) onto a keyring entry.
    /// </summary>
    internal sealed class SourceConfiguration
    {
        /// <summary>
        /// Case-insensitive prefix of the requested absolute URI. Use a host-level prefix
        /// (e.g. <c>https://pkgs.example.com/</c>) so that package download URIs match too,
        /// not just <c>index.json</c>. The longest matching prefix wins.
        /// </summary>
        public string UriPrefix { get; set; } = string.Empty;

        /// <summary>Name of the keyring entry holding the password / PAT.</summary>
        public string Entry { get; set; } = string.Empty;

        /// <summary>
        /// Username sent to NuGet alongside the password. Not a secret, so it lives in this
        /// file rather than the keyring. Falls back to <see cref="ProviderConfiguration.DefaultUsername"/>.
        /// </summary>
        public string? Username { get; set; }

        /// <summary>Overrides <see cref="KeyringCommandConfiguration.Folder"/> for this source.</summary>
        public string? Folder { get; set; }

        /// <summary>Overrides <see cref="KeyringCommandConfiguration.Wallet"/> for this source.</summary>
        public string? Wallet { get; set; }

        /// <summary>
        /// Authentication types reported to NuGet. <c>basic</c> is correct for
        /// password/PAT credentials, which is what a keyring entry holds.
        /// </summary>
        public List<string>? AuthenticationTypes { get; set; }

        /// <summary>A short label used in log messages. Defaults to <see cref="UriPrefix"/>.</summary>
        public string? Name { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? UriPrefix : Name!;
    }

    internal sealed class ProviderConfiguration
    {
        public KeyringCommandConfiguration Keyring { get; set; } = new KeyringCommandConfiguration();

        /// <summary>Username used for sources that do not specify one.</summary>
        public string? DefaultUsername { get; set; }

        public List<SourceConfiguration> Sources { get; set; } = new List<SourceConfiguration>();

        /// <summary>Path this configuration was loaded from; <c>null</c> for defaults.</summary>
        [JsonIgnore]
        public string? FilePath { get; set; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Keyring.Executable))
            {
                throw new ConfigurationException("keyring.executable must not be empty.");
            }

            if (Keyring.TimeoutSeconds <= 0)
            {
                throw new ConfigurationException("keyring.timeoutSeconds must be greater than zero.");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SourceConfiguration source in Sources)
            {
                if (string.IsNullOrWhiteSpace(source.UriPrefix))
                {
                    throw new ConfigurationException("Every entry in sources needs a uriPrefix.");
                }

                if (string.IsNullOrWhiteSpace(source.Entry))
                {
                    throw new ConfigurationException(
                        $"Source '{source.DisplayName}' needs an entry (the keyring entry name).");
                }

                if (!seen.Add(source.UriPrefix))
                {
                    throw new ConfigurationException(
                        $"Duplicate uriPrefix '{source.UriPrefix}' in sources.");
                }
            }
        }

        /// <summary>
        /// Finds the most specific configured source for <paramref name="uri"/>, or <c>null</c>
        /// when this provider is not responsible for it.
        /// </summary>
        public SourceConfiguration? FindSource(Uri uri)
        {
            string target = uri.AbsoluteUri;
            SourceConfiguration? best = null;

            foreach (SourceConfiguration source in Sources)
            {
                if (!target.StartsWith(source.UriPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (best is null || source.UriPrefix.Length > best.UriPrefix.Length)
                {
                    best = source;
                }
            }

            return best;
        }
    }
}
