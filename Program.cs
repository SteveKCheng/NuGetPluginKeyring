using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NuGetPluginKeyring.Configuration;
using NuGetPluginKeyring.Keyring;
using NuGetPluginKeyring.Logging;
using NuGetPluginKeyring.Plugin;
using NuGet.Common;

namespace NuGetPluginKeyring
{
    internal static class Program
    {
        /// <summary>Path of a file to append diagnostics to.</summary>
        private const string LogFileVariable = "KEYRING_NUGET_CREDPROVIDER_LOG";

        /// <summary>Minimum log level, e.g. <c>Verbose</c> or <c>Debug</c>.</summary>
        private const string LogLevelVariable = "KEYRING_NUGET_CREDPROVIDER_LOGLEVEL";

        private const int ExitSuccess = 0;
        private const int ExitUsage = 1;
        private const int ExitNoCredentials = 2;
        private const int ExitError = 3;

        public static async Task<int> Main(string[] args)
        {
            CommandLine commandLine;

            try
            {
                commandLine = CommandLine.Parse(args);
            }
            catch (ConfigurationException exception)
            {
                Console.Error.WriteLine(exception.Message);
                Console.Error.WriteLine();
                WriteUsage();
                return ExitUsage;
            }

            if (commandLine.ShowHelp)
            {
                WriteUsage();
                return ExitSuccess;
            }

            using var logger = new MultiProviderLogger();
            AddStartupSinks(logger, commandLine);

            using var cancellation = new CancellationTokenSource();

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            ProviderConfiguration configuration;
            string? configurationError = null;

            try
            {
                configuration = ConfigurationLoader.Load(commandLine.ConfigPath);
            }
            catch (ConfigurationException exception)
            {
                logger.Error(exception.Message);

                // In plugin mode a bad configuration must not break every restore: serve nothing,
                // but remember the problem so it can be reported over the connection once one
                // exists. Otherwise a typo is invisible unless a log file was configured.
                if (!commandLine.Plugin)
                {
                    return ExitError;
                }

                configurationError = exception.Message;
                configuration = new ProviderConfiguration();
            }

            logger.Verbose(
                configuration.FilePath is null
                    ? "No configuration file found; probed: "
                      + string.Join(", ", ConfigurationLoader.CandidatePaths())
                    : $"Loaded configuration from '{configuration.FilePath}' "
                      + $"({configuration.Sources.Count} source(s)).");

            var reader = new KeyringReader(configuration.Keyring, logger);
            var resolver = new CredentialResolver(configuration, reader, logger, configurationError);

            if (commandLine.Plugin)
            {
                return await PluginHost
                    .RunAsync(resolver, logger, cancellation.Token)
                    .ConfigureAwait(false);
            }

            return await RunStandaloneAsync(
                commandLine, configuration, resolver, logger, cancellation.Token)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Diagnostic mode. Lets the keyring command and the URI-to-entry mapping be checked
        /// without involving NuGet at all.
        /// </summary>
        private static async Task<int> RunStandaloneAsync(
            CommandLine commandLine,
            ProviderConfiguration configuration,
            CredentialResolver resolver,
            IProviderLogger logger,
            CancellationToken cancellationToken)
        {
            if (commandLine.ListSources)
            {
                WriteSources(configuration);
                return ExitSuccess;
            }

            if (commandLine.Uri is null)
            {
                Console.Error.WriteLine("Specify --uri <source uri>, or --list-sources.");
                Console.Error.WriteLine();
                WriteUsage();
                return ExitUsage;
            }

            if (commandLine.DryRun)
            {
                KeyringCommand? command =
                    resolver.DescribeCommand(commandLine.Uri, out SourceConfiguration? source);

                if (command is null || source is null)
                {
                    Console.Error.WriteLine(
                        $"No configured source matches '{commandLine.Uri.AbsoluteUri}'.");
                    return ExitNoCredentials;
                }

                Console.WriteLine($"source:   {source.DisplayName}");
                Console.WriteLine($"entry:    {source.Entry}");
                Console.WriteLine(
                    "username: "
                    + (source.Username ?? configuration.DefaultUsername ?? "<unset>"));
                Console.WriteLine($"command:  {command}");
                return ExitSuccess;
            }

            CredentialResult result = await resolver
                .ResolveAsync(commandLine.Uri, commandLine.IsRetry, cancellationToken)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case CredentialOutcome.Success:
                    Console.WriteLine($"Username: {result.Username}");
                    Console.WriteLine(
                        commandLine.ShowSecret
                            ? $"Password: {result.Password}"
                            : $"Password: <{result.Password!.Length} characters; "
                              + "pass --show-secret to print it>");
                    return ExitSuccess;

                case CredentialOutcome.NotFound:
                    Console.Error.WriteLine($"No credentials found. {result.Message}");
                    return ExitNoCredentials;

                default:
                    Console.Error.WriteLine($"Error. {result.Message}");
                    return ExitError;
            }
        }

        private static void WriteSources(ProviderConfiguration configuration)
        {
            Console.WriteLine(
                configuration.FilePath is null
                    ? "No configuration file found. Probed:"
                      + System.Environment.NewLine + "  "
                      + string.Join(
                          System.Environment.NewLine + "  ",
                          ConfigurationLoader.CandidatePaths())
                    : $"Configuration: {configuration.FilePath}");

            Console.WriteLine();
            Console.WriteLine($"keyring executable: {configuration.Keyring.Executable}");
            Console.WriteLine($"default username:   {configuration.DefaultUsername ?? "<unset>"}");
            Console.WriteLine();

            if (configuration.Sources.Count == 0)
            {
                Console.WriteLine("No sources configured.");
                return;
            }

            foreach (SourceConfiguration source in configuration.Sources)
            {
                Console.WriteLine($"- {source.DisplayName}");
                Console.WriteLine($"    uriPrefix: {source.UriPrefix}");
                Console.WriteLine($"    entry:     {source.Entry}");
                Console.WriteLine(
                    "    username:  "
                    + (source.Username ?? configuration.DefaultUsername ?? "<unset>"));
            }
        }

        /// <summary>
        /// Adds the sinks that exist before a plugin connection does.
        /// <para>
        /// Nothing may be written to stdout in plugin mode — it carries the protocol — so the
        /// console sink is stderr, and only in standalone mode.
        /// </para>
        /// </summary>
        private static void AddStartupSinks(MultiProviderLogger logger, CommandLine commandLine)
        {
            LogLevel level = ResolveLogLevel(commandLine);

            string? logFile = System.Environment.GetEnvironmentVariable(LogFileVariable);

            if (!string.IsNullOrWhiteSpace(logFile))
            {
                TextWriterProviderLogger? fileLogger =
                    TextWriterProviderLogger.TryCreateFileLogger(logFile!);

                if (fileLogger is not null)
                {
                    fileLogger.MinimumLevel = level;
                    logger.Add(fileLogger);
                }
            }

            // In plugin mode stderr is captured by NuGet but not shown, so only add it when the
            // user explicitly asked for verbosity or we are running standalone.
            if (!commandLine.Plugin || commandLine.Verbose)
            {
                var stderrLogger = new TextWriterProviderLogger(
                    Console.Error, ownsWriter: false, timestamps: commandLine.Plugin)
                {
                    MinimumLevel = level,
                };

                logger.Add(stderrLogger);
            }
        }

        private static LogLevel ResolveLogLevel(CommandLine commandLine)
        {
            if (commandLine.Verbose)
            {
                return LogLevel.Verbose;
            }

            string? configured = System.Environment.GetEnvironmentVariable(LogLevelVariable);

            if (!string.IsNullOrWhiteSpace(configured)
                && Enum.TryParse(configured, ignoreCase: true, out LogLevel parsed))
            {
                return parsed;
            }

            return LogLevel.Information;
        }

        private static void WriteUsage()
        {
            Console.Error.WriteLine(
@"nuget-plugin-keyring - sources NuGet credentials from a Linux keyring.

Normally NuGet launches this itself with -Plugin; the other modes are for
setting it up and diagnosing it.

  -Plugin                 Run as a NuGet plugin, speaking the plugin protocol
                          over stdin/stdout. NuGet passes this.
  --uri <uri>             Resolve credentials for a source URI and print them.
  --dry-run               With --uri: print the keyring command that would run,
                          without running it.
  --show-secret           With --uri: print the password instead of masking it.
  --is-retry              With --uri: behave as if NuGet had rejected the
                          previously supplied credentials.
  --list-sources          Print the loaded configuration.
  --config <path>         Use this configuration file.
  -v, --verbose           Verbose diagnostics on stderr.
  -h, --help              This text.

Environment variables:
  " + ConfigurationLoader.ConfigPathVariable + @"   Configuration file path.
  " + LogFileVariable + @"      Append diagnostics to this file.
  " + LogLevelVariable + @" Minimum log level (Debug, Verbose,
                                          Information, Minimal, Warning, Error).

Configuration file (first match wins):
  " + string.Join(System.Environment.NewLine + "  ", ConfigurationLoader.CandidatePaths()));
        }

        private sealed class CommandLine
        {
            public bool Plugin { get; private set; }

            public bool ShowHelp { get; private set; }

            public bool Verbose { get; private set; }

            public bool DryRun { get; private set; }

            public bool ShowSecret { get; private set; }

            public bool IsRetry { get; private set; }

            public bool ListSources { get; private set; }

            public Uri? Uri { get; private set; }

            public string? ConfigPath { get; private set; }

            public static CommandLine Parse(string[] args)
            {
                var parsed = new CommandLine();

                if (args.Length == 0)
                {
                    parsed.ShowHelp = true;
                    return parsed;
                }

                for (int index = 0; index < args.Length; index++)
                {
                    string argument = args[index];

                    switch (argument.ToLowerInvariant())
                    {
                        case "-plugin":
                        case "--plugin":
                            parsed.Plugin = true;
                            break;

                        case "-h":
                        case "--help":
                        case "-?":
                            parsed.ShowHelp = true;
                            break;

                        case "-v":
                        case "--verbose":
                            parsed.Verbose = true;
                            break;

                        case "--dry-run":
                            parsed.DryRun = true;
                            break;

                        case "--show-secret":
                            parsed.ShowSecret = true;
                            break;

                        case "--is-retry":
                            parsed.IsRetry = true;
                            break;

                        case "--list-sources":
                            parsed.ListSources = true;
                            break;

                        case "--uri":
                            parsed.Uri = ParseUri(Next(args, ref index, "--uri"));
                            break;

                        case "--config":
                            parsed.ConfigPath = Next(args, ref index, "--config");
                            break;

                        default:
                            throw new ConfigurationException(
                                $"Unrecognized argument '{argument}'.");
                    }
                }

                return parsed;
            }

            private static string Next(string[] args, ref int index, string option)
            {
                if (index + 1 >= args.Length)
                {
                    throw new ConfigurationException($"{option} requires a value.");
                }

                return args[++index];
            }

            private static Uri ParseUri(string value)
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                {
                    throw new ConfigurationException($"'{value}' is not an absolute URI.");
                }

                return uri;
            }
        }
    }
}
