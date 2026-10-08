using System;
using System.Collections.Generic;
using NuGet.Common;

namespace KeyringNuGetCredProvider.Logging
{
    /// <summary>
    /// Fans messages out to several sinks. Sinks are added over the process lifetime: the
    /// stderr/file sinks exist from startup, the connection sink only once NuGet has completed
    /// the handshake.
    /// <para>
    /// Each sink applies its own <see cref="IProviderLogger.MinimumLevel"/>, because they differ:
    /// the connection sink must stay silent until NuGet sends <c>SetLogLevel</c>, while a file
    /// sink enabled for debugging wants everything.
    /// </para>
    /// </summary>
    internal sealed class MultiProviderLogger : IProviderLogger, IDisposable
    {
        private readonly List<IProviderLogger> _loggers = new List<IProviderLogger>();
        private readonly object _gate = new object();

        /// <summary>Process-wide floor. Defaults to <see cref="LogLevel.Debug"/> so that the
        /// per-sink levels are what actually decide.</summary>
        public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

        public void Add(IProviderLogger logger)
        {
            lock (_gate)
            {
                _loggers.Add(logger);
            }
        }

        public void Log(LogLevel level, string message)
        {
            if (level < MinimumLevel)
            {
                return;
            }

            IProviderLogger[] snapshot;

            lock (_gate)
            {
                snapshot = _loggers.ToArray();
            }

            foreach (IProviderLogger logger in snapshot)
            {
                try
                {
                    logger.Log(level, message);
                }
                catch (Exception)
                {
                    // A broken sink (closed connection, unwritable file) must never take down
                    // credential acquisition.
                }
            }
        }

        public void Dispose()
        {
            IProviderLogger[] snapshot;

            lock (_gate)
            {
                snapshot = _loggers.ToArray();
                _loggers.Clear();
            }

            foreach (IProviderLogger logger in snapshot)
            {
                (logger as IDisposable)?.Dispose();
            }
        }
    }
}
