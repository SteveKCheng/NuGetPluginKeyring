# nuget-plugin-keyring

A NuGet credential provider that reads package-source passwords from a Linux keyring by
launching a query executable (`kwallet-query` by default), so that `NuGet.Config` need not
contain cleartext credentials.

NuGet's own password encryption is Windows-only. On Linux the usual workaround is a
cleartext `<packageSourceCredentials>` entry. This plugin replaces that with a keyring
lookup, which means configuration files stay safe to share.

## How it hooks into NuGet

NuGet 4.8+ supports out-of-process plugins that speak a JSON protocol over stdin/stdout.
Protocol version 2.0.0 added the `Authentication` operation claim, which is what a
credential provider implements. We use the `NuGet.Protocol` package so the wire protocol,
handshake and message dispatch are handled for us; the interesting part is just a handful
of request handlers.

Message flow for one restore:

| Direction | Message | What we do |
| --- | --- | --- |
| both | `Handshake` | Handled by `PluginFactory`; negotiates 2.0.0 |
| NuGet → us | `Initialize` | Acknowledge, log the client version |
| NuGet → us | `GetOperationClaims` | Claim `Authentication` for the source-agnostic request, nothing otherwise |
| NuGet → us | `SetLogLevel` | From here on we may log to NuGet's console |
| NuGet → us | `SetCredentials` | Acknowledged; we source everything from the keyring |
| NuGet → us | `GetAuthenticationCredentials` | Map URI → keyring entry, run the query tool, answer |
| NuGet → us | `Close` | Handled by `PluginFactory`; we exit |

Two details worth knowing, because neither is obvious from the documentation:

* `GetOperationClaims` is sent **twice**. Once with no package source and no service index —
  that is the "are you an authentication provider?" question, and the only one we answer
  `Authentication` to. Once per source, for download-related claims, which we decline.
  (`GetOperationClaimsRequest.ServiceIndex` is obsolete in NuGet.Protocol 7.x and always
  returns `null`; use `ServiceIndexJson`.)
* Requests time out after 5 seconds unless the plugin sends progress messages, so the
  credential handler wraps its work in an `AutomaticProgressReporter`.

**stdout belongs to the protocol.** Nothing may be written to it in plugin mode. All
diagnostics go to stderr, to a log file, or to NuGet over the connection.

## Non-interactive by design

A keyring read neither prompts nor blocks, so the credential handler serves requests even
when NuGet sets `IsNonInteractive`. That is the point: a plain `dotnet restore` works,
with no `--interactive`, and no device-flow prompt.

The query process is still killed after `keyring.timeoutSeconds` in case the wallet is
locked and the tool decides to prompt after all.

## Building and installing

```bash
dotnet publish -c Release -o out
```

Then pick one of the three discovery mechanisms NuGet supports (all three are verified to
work with this plugin):

1. **On `PATH`** (NuGet 6.13+) — the output is named `nuget-plugin-keyring`, which matches
   the required `nuget-plugin-*` pattern, so simply putting `out/` on `PATH` is enough.
2. **Convention-based** — copy the publish output to
   `~/.nuget/plugins/netcore/nuget-plugin-keyring/`. The folder name must match the
   assembly name.
3. **Environment variable** — `export NUGET_PLUGIN_PATHS=/abs/path/to/nuget-plugin-keyring`
   (absolute paths only). Handy for trying it out without installing.

## Configuration

Copy `config.sample.json` to `~/.config/NuGetPluginKeyring/config.json`
(`$XDG_CONFIG_HOME` is honoured) and edit it. `KEYRING_NUGET_CREDPROVIDER_CONFIG` overrides
the location. JSON comments and trailing commas are accepted.

```jsonc
{
  "keyring": {
    "executable": "kwallet-query",
    "arguments": [
      ["--read-password", "{entry}"],
      ["--folder", "{folder}"],
      ["{wallet}"]
    ],
    "folder": "Passwords",
    "wallet": "kdewallet",
    "timeoutSeconds": 10
  },
  "defaultUsername": "VssSessionToken",
  "sources": [
    {
      "uriPrefix": "https://nuget.example.com/",
      "entry": "nuget-example-internal",
      "username": "steve"
    }
  ]
}
```

### The argument template

> The default arguments are a guess at `kwallet-query`'s command line. **Verify them**
> against your version before relying on them — `--dry-run` below prints exactly what
> would be executed, and nothing in the code assumes a particular shape.

Placeholders, substituted in `executable` and in every argument:

| Placeholder | Value |
| --- | --- |
| `{entry}` | the keyring entry name for the matched source |
| `{username}` | the username for the matched source |
| `{folder}` | the source's `folder`, else `keyring.folder` |
| `{wallet}` | the source's `wallet`, else `keyring.wallet` |
| `{uri}` | the full URI NuGet asked about |
| `{host}` | the host of that URI |

An array element may itself be an array, which makes it an all-or-nothing group: the group
is dropped if any placeholder inside it is unset. That is how `["--folder", "{folder}"]`
disappears cleanly when no folder is configured, instead of leaving a dangling flag.

Arguments are passed as separate argv elements with no shell involved, so entry names
containing spaces or shell metacharacters are safe.

### Matching sources

NuGet's credential request carries only a **URI**, never the source *name* from
`NuGet.Config`. So `uriPrefix` matches against the requested absolute URI,
case-insensitively, and the longest matching prefix wins.

Use a **host-level prefix** such as `https://nuget.example.com/`. NuGet asks for
credentials for package download URIs too, not just `index.json`, and those often sit on a
different path (sometimes a different host — add an entry for each).

### Username

The keyring entry supplies only the password, so the username comes from `username` on the
source, falling back to `defaultUsername`. A username is not a secret, so keeping it in
this file loses nothing. Many feeds accept any username alongside a PAT; Azure DevOps
conventionally uses `VssSessionToken`.

## Diagnostics

`--dry-run` resolves everything except the actual lookup, which makes it the fastest way to
check both the mapping and the argument template:

```console
$ nuget-plugin-keyring --uri https://nuget.example.com/v3/index.json --dry-run
source:   example-internal
entry:    nuget-example-internal
username: steve
command:  kwallet-query --read-password nuget-example-internal --folder Passwords kdewallet
```

Drop `--dry-run` to perform the lookup (the password is masked unless `--show-secret` is
given), `--list-sources` to dump the loaded configuration, and `--is-retry` to simulate
NuGet having rejected the credentials.

To watch the plugin while NuGet drives it:

```bash
export KEYRING_NUGET_CREDPROVIDER_LOG=/tmp/keyring-credprovider.log
export KEYRING_NUGET_CREDPROVIDER_LOGLEVEL=Verbose
dotnet restore
```

Secrets are never logged — only their length.

### Environment variables

| Variable | Meaning |
| --- | --- |
| `KEYRING_NUGET_CREDPROVIDER_CONFIG` | Configuration file path |
| `KEYRING_NUGET_CREDPROVIDER_LOG` | Append diagnostics to this file |
| `KEYRING_NUGET_CREDPROVIDER_LOGLEVEL` | `Debug`, `Verbose`, `Information`, `Minimal`, `Warning`, `Error` |

NuGet's own variables are useful too: `NUGET_PLUGIN_PATHS` to point at the plugin, and
`NUGET_PLUGINS_CACHE_PATH` for the operation-claims cache, which is kept for 30 days and
can otherwise mask changes while experimenting (`dotnet nuget locals plugins-cache --clear`).

## Behaviour on failure

| Situation | Response to NuGet | Effect |
| --- | --- | --- |
| No configured source matches the URI | `NotFound` | NuGet falls through to its other credential sources |
| No configuration file | `NotFound` | The plugin is inert; restores behave as if it were not installed |
| Unparseable configuration | `NotFound` + a warning in the restore output | Restores keep working |
| Entry exists but holds nothing | `NotFound` | NuGet falls through |
| Query tool fails or times out | `Error` with the tool's stderr | Surfaced on the restore |
| NuGet reports the credentials were rejected (`IsRetry`) | re-read, plus a warning naming the entry | NuGet's bounded retries then give up |

Nothing here is cached by the plugin. NuGet caches the credential for the duration of a
restore, so one restore performs one keyring lookup per source.

## Author's note

All of the above was written by Claude.  You can say this project is over-engineered, and perhaps 
so is this whole plug-in mechanism from NuGet.  I had thought the project would just be one C# 
script, but apparently it needs all sorts of internal classes.

I am putting it up anyway in case anyone finds it useful.

