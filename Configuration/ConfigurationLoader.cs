using System;
using System.IO;
using System.Text.Json;

namespace KeyringNuGetCredProvider.Configuration
{
    internal static class ConfigurationLoader
    {
        /// <summary>Points at an explicit configuration file, overriding the default locations.</summary>
        public const string ConfigPathVariable = "KEYRING_NUGET_CREDPROVIDER_CONFIG";

        private const string DirectoryName = "KeyringNuGetCredProvider";
        private const string FileName = "config.json";

        public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

        private static JsonSerializerOptions CreateSerializerOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                WriteIndented = true,
            };

            options.Converters.Add(new ArgumentTemplateConverter());
            return options;
        }

        /// <summary>
        /// Configuration file locations in precedence order. The first existing file wins.
        /// </summary>
        public static string[] CandidatePaths()
        {
            string? explicitPath = Environment.GetEnvironmentVariable(ConfigPathVariable);
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return new[] { explicitPath! };
            }

            string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string configHome = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config")
                : xdg!;

            return new[] { Path.Combine(configHome, DirectoryName, FileName) };
        }

        /// <summary>
        /// Loads the configuration. When <paramref name="path"/> is <c>null</c> the default
        /// locations are probed; a missing file yields an empty (but valid) configuration so
        /// that the plugin stays inert rather than failing every restore.
        /// </summary>
        public static ProviderConfiguration Load(string? path)
        {
            string[] candidates = path is null
                ? CandidatePaths()
                : new[] { path };

            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                ProviderConfiguration configuration = Parse(File.ReadAllText(candidate), candidate);
                configuration.FilePath = candidate;
                return configuration;
            }

            if (path is not null)
            {
                throw new ConfigurationException($"Configuration file '{path}' does not exist.");
            }

            return new ProviderConfiguration();
        }

        public static ProviderConfiguration Parse(string json, string origin)
        {
            ProviderConfiguration? configuration;

            try
            {
                configuration = JsonSerializer.Deserialize<ProviderConfiguration>(json, SerializerOptions);
            }
            catch (JsonException exception)
            {
                throw new ConfigurationException(
                    $"Could not parse '{origin}': {exception.Message}", exception);
            }

            if (configuration is null)
            {
                throw new ConfigurationException($"'{origin}' is empty.");
            }

            configuration.Validate();
            return configuration;
        }
    }
}
