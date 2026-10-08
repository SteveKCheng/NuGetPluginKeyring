using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuGetPluginKeyring.Configuration
{
    /// <summary>
    /// One entry of the <c>arguments</c> array in the configuration file.
    /// <para>
    /// A template is either a single token (<c>"--read-password"</c>) or an all-or-nothing
    /// group of tokens (<c>["--folder", "{folder}"]</c>).  A group is dropped in its entirety
    /// when any placeholder inside it resolves to an empty value, which keeps optional flags
    /// such as <c>--folder</c> from being emitted without their value.
    /// </para>
    /// </summary>
    internal sealed class ArgumentTemplate
    {
        public ArgumentTemplate(IReadOnlyList<string> tokens)
        {
            Tokens = tokens;
        }

        public IReadOnlyList<string> Tokens { get; }

        /// <summary>
        /// Expands every token against <paramref name="placeholders"/> and appends the result to
        /// <paramref name="target"/>.  Returns <c>false</c> (appending nothing) when the group has
        /// to be dropped because one of its placeholders is unset.
        /// </summary>
        public bool TryExpand(IReadOnlyDictionary<string, string?> placeholders, List<string> target)
        {
            var expanded = new List<string>(Tokens.Count);

            foreach (string token in Tokens)
            {
                if (!TryExpandToken(token, placeholders, out string? value))
                {
                    return false;
                }

                expanded.Add(value!);
            }

            target.AddRange(expanded);
            return true;
        }

        private static bool TryExpandToken(
            string token,
            IReadOnlyDictionary<string, string?> placeholders,
            out string? result)
        {
            result = null;

            // Fast path: no placeholder at all.
            if (token.IndexOf('{') < 0)
            {
                result = token;
                return true;
            }

            var builder = new System.Text.StringBuilder(token.Length);
            int index = 0;

            while (index < token.Length)
            {
                char current = token[index];

                if (current != '{')
                {
                    builder.Append(current);
                    index++;
                    continue;
                }

                int end = token.IndexOf('}', index + 1);
                if (end < 0)
                {
                    // Unterminated brace: treat the remainder as a literal.
                    builder.Append(token, index, token.Length - index);
                    break;
                }

                string name = token.Substring(index + 1, end - index - 1);

                if (!placeholders.TryGetValue(name, out string? value))
                {
                    throw new ConfigurationException(
                        $"Unknown placeholder '{{{name}}}' in argument '{token}'. Known placeholders: "
                        + string.Join(", ", KnownPlaceholderNames(placeholders)) + ".");
                }

                if (string.IsNullOrEmpty(value))
                {
                    // Unset placeholder -> drop the whole group.
                    return false;
                }

                builder.Append(value);
                index = end + 1;
            }

            result = builder.ToString();
            return true;
        }

        private static IEnumerable<string> KnownPlaceholderNames(
            IReadOnlyDictionary<string, string?> placeholders)
        {
            foreach (string key in placeholders.Keys)
            {
                yield return "{" + key + "}";
            }
        }

        public override string ToString() => string.Join(" ", Tokens);
    }

    /// <summary>
    /// Accepts either a bare string or an array of strings for a single <see cref="ArgumentTemplate"/>.
    /// </summary>
    internal sealed class ArgumentTemplateConverter : JsonConverter<ArgumentTemplate>
    {
        public override ArgumentTemplate Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return new ArgumentTemplate(new[] { reader.GetString() ?? string.Empty });
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var tokens = new List<string>();

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        throw new JsonException(
                            "An argument group must contain only strings.");
                    }

                    tokens.Add(reader.GetString() ?? string.Empty);
                }

                return new ArgumentTemplate(tokens);
            }

            throw new JsonException(
                "An argument must be either a string or an array of strings.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            ArgumentTemplate value,
            JsonSerializerOptions options)
        {
            if (value.Tokens.Count == 1)
            {
                writer.WriteStringValue(value.Tokens[0]);
                return;
            }

            writer.WriteStartArray();
            foreach (string token in value.Tokens)
            {
                writer.WriteStringValue(token);
            }
            writer.WriteEndArray();
        }
    }
}
