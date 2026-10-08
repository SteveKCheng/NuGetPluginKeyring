using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NuGetPluginKeyring.Configuration;
using NuGetPluginKeyring.Logging;

namespace NuGetPluginKeyring.Keyring
{
    /// <summary>
    /// Reads secrets by launching an external query executable (by default
    /// <c>kwallet-query</c>) and taking the first non-empty line of its stdout.
    /// <para>
    /// No shell is involved: the entry name is passed as a distinct argv element, so entry
    /// names needing quoting or containing shell metacharacters are harmless.
    /// </para>
    /// </summary>
    internal sealed class KeyringReader
    {
        private readonly KeyringCommandConfiguration _configuration;
        private readonly IProviderLogger _logger;

        public KeyringReader(KeyringCommandConfiguration configuration, IProviderLogger logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Builds the command line for a source without running it.
        /// </summary>
        public KeyringCommand BuildCommand(SourceConfiguration source, Uri uri, string? username)
        {
            var placeholders = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["entry"] = source.Entry,
                ["username"] = username,
                ["folder"] = source.Folder ?? _configuration.Folder,
                ["wallet"] = source.Wallet ?? _configuration.Wallet,
                ["uri"] = uri.AbsoluteUri,
                ["host"] = uri.Host,
            };

            var arguments = new List<string>();

            foreach (ArgumentTemplate template in _configuration.Arguments)
            {
                if (!template.TryExpand(placeholders, arguments))
                {
                    _logger.Debug($"Dropping argument group '{template}': a placeholder is unset.");
                }
            }

            string executable = ExpandExecutable(placeholders);
            return new KeyringCommand(executable, arguments);
        }

        private string ExpandExecutable(IReadOnlyDictionary<string, string?> placeholders)
        {
            var expanded = new List<string>(1);
            var template = new ArgumentTemplate(new[] { _configuration.Executable });

            if (!template.TryExpand(placeholders, expanded) || expanded.Count != 1)
            {
                throw new ConfigurationException(
                    $"keyring.executable '{_configuration.Executable}' expanded to nothing.");
            }

            return expanded[0];
        }

        public async Task<KeyringLookupResult> ReadAsync(
            KeyringCommand command,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command.Executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string argument in command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            foreach (KeyValuePair<string, string> variable in _configuration.Environment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }

            _logger.Verbose($"Running keyring query: {command}");

            using var process = new Process { StartInfo = startInfo };

            try
            {
                if (!process.Start())
                {
                    return KeyringLookupResult.Failed(
                        $"Could not start '{command.Executable}'.");
                }
            }
            catch (Exception exception)
            {
                return KeyringLookupResult.Failed(
                    $"Could not start '{command.Executable}': {exception.Message}");
            }

            // The query tool must not be able to block us on a prompt.
            process.StandardInput.Close();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(_configuration.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token, cancellationToken);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);

                if (timeout.IsCancellationRequested)
                {
                    return KeyringLookupResult.Failed(
                        $"'{command.Executable}' did not finish within "
                        + $"{_configuration.TimeoutSeconds}s and was terminated. "
                        + "If it is waiting for a wallet-unlock prompt, unlock the wallet first "
                        + "or raise keyring.timeoutSeconds.");
                }

                throw;
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = (await stderrTask.ConfigureAwait(false)).Trim();

            if (process.ExitCode != 0)
            {
                return KeyringLookupResult.Failed(
                    $"'{command.Executable}' exited with code {process.ExitCode}."
                    + (stderr.Length > 0 ? $" stderr: {stderr}" : string.Empty));
            }

            if (stderr.Length > 0)
            {
                _logger.Verbose($"'{command.Executable}' wrote to stderr: {stderr}");
            }

            string? secret = FirstNonEmptyLine(stdout);

            if (secret is null)
            {
                return KeyringLookupResult.NotFound(
                    $"'{command.Executable}' succeeded but returned no value."
                    + (stderr.Length > 0 ? $" stderr: {stderr}" : string.Empty));
            }

            _logger.Verbose(
                $"Keyring returned a secret of {secret.Length} characters.");

            return KeyringLookupResult.Found(secret);
        }

        /// <summary>
        /// The query tool is documented to print the secret on a single line. Blank leading
        /// lines and the trailing newline are ignored; anything after the first non-empty line
        /// is discarded so that a chatty tool does not corrupt the secret.
        /// </summary>
        internal static string? FirstNonEmptyLine(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            foreach (string line in output.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');

                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return null;
        }

        private static void KillQuietly(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Already gone, or not ours to kill.
            }
        }
    }
}
