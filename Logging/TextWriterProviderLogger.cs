using System;
using System.IO;
using NuGet.Common;

namespace NuGetPluginKeyring.Logging
{
    /// <summary>
    /// Writes to a <see cref="TextWriter"/> — stderr, or a log file.
    /// </summary>
    internal sealed class TextWriterProviderLogger : IProviderLogger, IDisposable
    {
        private readonly TextWriter _writer;
        private readonly bool _ownsWriter;
        private readonly bool _timestamps;
        private readonly object _gate = new object();

        public TextWriterProviderLogger(TextWriter writer, bool ownsWriter, bool timestamps)
        {
            _writer = writer;
            _ownsWriter = ownsWriter;
            _timestamps = timestamps;
        }

        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

        public static TextWriterProviderLogger? TryCreateFileLogger(string path)
        {
            try
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

                var writer = new StreamWriter(stream) { AutoFlush = true };
                return new TextWriterProviderLogger(writer, ownsWriter: true, timestamps: true);
            }
            catch (Exception)
            {
                // Logging is best effort; never fail startup over it.
                return null;
            }
        }

        public void Log(LogLevel level, string message)
        {
            if (level < MinimumLevel)
            {
                return;
            }

            string prefix = _timestamps
                ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-11} pid:{System.Environment.ProcessId}] "
                : $"[keyring-credprovider {level}] ";

            lock (_gate)
            {
                _writer.WriteLine(prefix + message);
                _writer.Flush();
            }
        }

        public void Dispose()
        {
            if (!_ownsWriter)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do while tearing down.
            }
        }
    }
}
