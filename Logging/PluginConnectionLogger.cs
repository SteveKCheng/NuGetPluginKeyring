using System;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Common;
using NuGet.Protocol.Plugins;

namespace KeyringNuGetCredProvider.Logging
{
    /// <summary>
    /// Forwards messages to the NuGet client over the plugin connection, which is the only way
    /// a plugin can get text in front of the user.
    /// <para>
    /// Per the protocol, logging is only permitted once NuGet has sent <c>SetLogLevel</c>; before
    /// that <see cref="MinimumLevel"/> is <see cref="LogLevel.Error"/> and nothing of lower
    /// severity is forwarded. Sends are fire-and-forget: a log message must never make a
    /// credential request time out.
    /// </para>
    /// </summary>
    internal sealed class PluginConnectionLogger : IProviderLogger
    {
        private readonly IConnection _connection;

        public PluginConnectionLogger(IConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public LogLevel MinimumLevel { get; set; } = LogLevel.Error;

        public void Log(LogLevel level, string message)
        {
            if (level < MinimumLevel)
            {
                return;
            }

            _ = SendAsync(level, message);
        }

        private async Task SendAsync(LogLevel level, string message)
        {
            try
            {
                await _connection.SendRequestAndReceiveResponseAsync<LogRequest, LogResponse>(
                    MessageMethod.Log,
                    new LogRequest(level, message),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection may already be closing; dropping a log line is acceptable.
            }
        }
    }
}
