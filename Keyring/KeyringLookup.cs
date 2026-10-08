using System;
using System.Collections.Generic;

namespace KeyringNuGetCredProvider.Keyring
{
    internal enum KeyringLookupStatus
    {
        /// <summary>A secret was returned.</summary>
        Found,

        /// <summary>The executable ran but produced no secret (unknown entry).</summary>
        NotFound,

        /// <summary>The executable could not be launched, failed, or timed out.</summary>
        Failed,
    }

    /// <summary>
    /// Resolved command line for a lookup, kept separate so that <c>--dry-run</c> can show it
    /// without executing anything.
    /// </summary>
    internal sealed class KeyringCommand
    {
        public KeyringCommand(string executable, IReadOnlyList<string> arguments)
        {
            Executable = executable;
            Arguments = arguments;
        }

        public string Executable { get; }

        public IReadOnlyList<string> Arguments { get; }

        /// <summary>A shell-quoted rendering, for logs and <c>--dry-run</c> output.</summary>
        public override string ToString()
        {
            var parts = new List<string>(Arguments.Count + 1) { Quote(Executable) };

            foreach (string argument in Arguments)
            {
                parts.Add(Quote(argument));
            }

            return string.Join(" ", parts);
        }

        private static string Quote(string value)
        {
            if (value.Length == 0)
            {
                return "''";
            }

            foreach (char c in value)
            {
                if (!char.IsLetterOrDigit(c) && "-_=+./:@,".IndexOf(c) < 0)
                {
                    return "'" + value.Replace("'", "'\\''") + "'";
                }
            }

            return value;
        }
    }

    internal sealed class KeyringLookupResult
    {
        private KeyringLookupResult(
            KeyringLookupStatus status,
            string? secret,
            string? diagnostics)
        {
            Status = status;
            Secret = secret;
            Diagnostics = diagnostics;
        }

        public KeyringLookupStatus Status { get; }

        /// <summary>The secret, only set when <see cref="Status"/> is
        /// <see cref="KeyringLookupStatus.Found"/>.</summary>
        public string? Secret { get; }

        /// <summary>Human-readable detail for logs. Never contains the secret.</summary>
        public string? Diagnostics { get; }

        public static KeyringLookupResult Found(string secret)
            => new KeyringLookupResult(KeyringLookupStatus.Found, secret, null);

        public static KeyringLookupResult NotFound(string diagnostics)
            => new KeyringLookupResult(KeyringLookupStatus.NotFound, null, diagnostics);

        public static KeyringLookupResult Failed(string diagnostics)
            => new KeyringLookupResult(KeyringLookupStatus.Failed, null, diagnostics);
    }
}
