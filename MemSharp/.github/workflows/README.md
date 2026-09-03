# CI workflows

> **These files must be moved to the repository root before they will run.**
>
> GitHub Actions only reads `.github/workflows/` at the root of a repository. MemSharp lives at
> `Vibe_Database/MemSharp/`, so a workflow left in `MemSharp/.github/workflows/` is silently
> ignored — no error, no run, nothing.

They are written for the monorepo layout already: path filters scope them to `MemSharp/**`, and
every job sets `working-directory: MemSharp`. Only their location needs to change.

```bash
# from the root of the Vibe_Database checkout
mkdir -p .github/workflows
cp MemSharp/.github/workflows/memsharp-ci.yml      .github/workflows/
cp MemSharp/.github/workflows/memsharp-release.yml .github/workflows/
```

The docs check stays where it is — `memsharp-ci.yml` invokes it at
`MemSharp/.github/scripts/check_docs.py`, which is a normal repository path and needs no move.

Names are prefixed `memsharp-` so they do not collide with other projects' workflows in the same
repository.

---

## memsharp-ci.yml

Runs on pushes and pull requests that touch `MemSharp/**`.

| Job | What it does |
|---|---|
| `test` | Restores, builds and runs the 214 tests on **ubuntu, windows and macos** |
| `sample` | Builds the Avalonia trading demo and re-renders the screenshots |
| `clients` | Starts a real server, then runs the Python, Node.js and Go client suites against it |
| `pack` | Builds both NuGet packages and uploads them as artifacts |
| `docs` | Verifies every relative link resolves and that `docs/en` and `docs/id` are in step |

**Why three operating systems for the engine.** Sharding, the sampling expiry sweeper and the
pipelines-based server all interact with the OS scheduler and its socket stack. A race that never
fires on Windows fires readily on Linux, and the concurrency tests exist precisely to catch that
class of bug.

**Why the sample only builds on Windows.** Avalonia's headless renderer needs Skia's native
dependencies; installing them on three runners to compile one sample is not worth the minutes.

**Why the clients run against a live server.** The only thing worth testing in a protocol client is
that its bytes match what the server actually sends back, and a mock cannot tell you that. The job
starts `memsharp serve`, polls the port until it answers, then runs all three suites against it.

**Why pack runs on every commit.** A packaging mistake — a missing readme, a broken license
expression, a project that should not have been packable — is cheap to find here and expensive to
find at release time, when NuGet versions are already immutable.

**A note on `dotnet test`.** The test step deliberately omits `--nologo`. The SDK forwards
unrecognised arguments through to the test runner, and Microsoft.Testing.Platform — which xunit.v3
uses — rejects `--nologo` with "Unknown option" and reports *zero tests ran* rather than failing
loudly. If you see a green-looking run with a total of 0, that is why.

## memsharp-release.yml

Triggered by a tag matching `memsharp-v*`.

```bash
# from the repository root, after updating <Version> in MemSharp/Directory.Build.props
git tag memsharp-v1.0.0
git push origin memsharp-v1.0.0
```

The workflow resolves the version from the tag, **fails if it disagrees with
`Directory.Build.props`**, then tests, packs, pushes to nuget.org and creates a GitHub release. The
version guard matters more than it looks: a package whose tag claims a different version cannot be
corrected afterwards, because published NuGet versions are immutable.

`workflow_dispatch` runs the same pipeline with `dry_run` defaulted to true, which builds and packs
without publishing — the way to rehearse a release.

### Required setup

| | |
|---|---|
| Secret `NUGET_API_KEY` | An API key from nuget.org, glob-scoped to `MemSharp*` so it covers both packages and nothing else |
| Environment `nuget` | Create under **Settings → Environments** with required reviewers. Publishing then needs a human approval even though pushing a tag does not |

Both packages are published together: `MemSharp` and `MemSharp.Cli`. The README tells users to
install the CLI, so shipping only the library would leave that instruction broken.

---

## Running the same checks locally

```bash
dotnet build -c Release
dotnet test tests/MemSharp.Tests/MemSharp.Tests.csproj -c Release
dotnet pack  -c Release                       # packages land in artifacts/packages/
dotnet run   -c Release --project samples/MemSharp.TradingDemo -- --capture docs/images
python .github/scripts/check_docs.py .

# clients, against a server you start yourself
memsharp serve --port 6391 --quiet &
python clients/python/test_client.py
node clients/nodejs/test/client.test.js
(cd clients/go && go test ./...)
```
