using NuGet.Common;

namespace NuGetPluginKeyring.Logging
{
    /// <summary>
    /// Minimal logging abstraction. Deliberately not <c>NuGet.Common.ILogger</c>: in plugin mode
    /// stdout belongs to the protocol, so every sink here has to be stderr, a file, or the
    /// plugin connection itself.
    /// </summary>
    internal interface IProviderLogger
    {
        LogLevel MinimumLevel { get; set; }

        void Log(LogLevel level, string message);
    }

    internal static class ProviderLoggerExtensions
    {
        public static void Debug(this IProviderLogger logger, string message)
            => logger.Log(LogLevel.Debug, message);

        public static void Verbose(this IProviderLogger logger, string message)
            => logger.Log(LogLevel.Verbose, message);

        public static void Information(this IProviderLogger logger, string message)
            => logger.Log(LogLevel.Information, message);

        public static void Minimal(this IProviderLogger logger, string message)
            => logger.Log(LogLevel.Minimal, message);

        public static void Warning(this IProviderLogger logger, string message)
            => logger.Log(LogLevel.Warning, message);

        public static void Error(this IProviderLogger logger, string message)
            => logger.Log(LogLevel.Error, message);
    }
}
