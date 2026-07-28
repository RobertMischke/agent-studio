# Public documentation truth check

The public repositories use four product names: **Agent Studio**, **Task
Server**, **Orchestrator Engine**, and **Runner**. `CodingAgentRunner` remains
the .NET package name, not the name of a separate product component.

Run the local wording and path check before publishing documentation:

```bash
python3 scripts/check-public-docs.py
```

The scheduled workflow also validates the recorded public package claims
against their registries. Run it locally when a package release or its public
documentation changes:

```bash
python3 scripts/check-public-docs.py --verify-registry
```

The check rejects retired personal repository links, the known personal Windows
profile path, and the retired two-word Runner product name. It intentionally
does not reject portable examples such as `%APPDATA%` or a path the reader owns.

## Registry facts verified on 2026-07-28

| Package | Registry | Published version checked | Public documentation location |
| --- | --- | --- | --- |
| `TokenEconomy` | NuGet | `0.2.0` | [`agent-orc/token-economy`](https://github.com/agent-orc/token-economy) README |
| `CodingAgentRunner` | NuGet | `0.6.0` | [`agent-orc/runner`](https://github.com/agent-orc/runner) package page |
| `coding-agent-chat` | npm | `0.3.2` | [`agent-orc/chat`](https://github.com/agent-orc/chat) README |

The table records published versions, not a promise that each one is the latest
release. The registry verification is the source of truth.

## Documented command catalog

This is an extraction of the public repository entry points. It is preparation
for the separately tracked fresh-machine/container onboarding run, not evidence
that the commands have been executed in a clean machine.

| Repository | Commands documented for a reader |
| --- | --- |
| Agent Studio | `./api.sh`; `cd frontend`; `npm install`; `npm start`; `git clone https://github.com/agent-orc/agent-studio.git`; `git clone https://github.com/agent-orc/chat.git C:/Projects/coding-agent-chat`; `npm run build`; `dotnet restore`; `dotnet run`; `npm start --prefix frontend` |
| Chat | `npm install coding-agent-chat`; `npm ci`; `npm run build`; `npm test`; `ng build coding-agent-chat --watch`; `npm install --save-exact coding-agent-chat@0.3.2` |
| Quality Studio | `dotnet run --project src/quality-cli -- scan . --include "**/*.cs"`; `dotnet run --project src/quality-cli -- boundaries scan .`; `dotnet run --project src/quality-cli -- diff . --base <base> --head <head> --fail-on-regression`; `dotnet run --project src/quality-cli -- security scan .`; `dotnet run --project src/quality-cli -- report . --format sarif --output quality-report.sarif` |
| Runner | `dotnet add package CodingAgentRunner`; `dotnet add package CodingAgentRunner.Rendering`; `dotnet build`; `dotnet test`; `dotnet run -c Release --project benchmarks/CodingAgentRunner.Benchmarks -- --filter '*'` |
| Token Economy | `dotnet add package TokenEconomy`; `dotnet add package TokenEconomy --version 0.2.0` |

The organization profile has no documented execution command.

## Fresh-machine status

Not verified in this sweep: the Agent Studio quickstart on a fresh Windows
machine, its full container onboarding path, external CLI sign-in, and package
installation/build execution. The sweep verified public repository content and
registry responses only.
