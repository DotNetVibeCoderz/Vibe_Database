# CI workflows

**The workflows now live at the repository root**, in `Vibe_Database/.github/workflows/`:

```
.github/workflows/memsharp-ci.yml
.github/workflows/memsharp-release.yml
```

They have to be there. GitHub Actions reads `.github/workflows/` only from the root of a repository,
so a workflow left in `MemSharp/.github/workflows/` is silently ignored — no error, no run, nothing.
This directory keeps only the docs check they invoke, at
`MemSharp/.github/scripts/check_docs.py`, which is a normal repository path and needs no move.

Names are prefixed `memsharp-` so they do not collide with other projects' workflows in the same
repository. Path filters scope them to `MemSharp/**`, and every job sets
`working-directory: MemSharp`, so they ignore changes to the sibling projects entirely.

> The sibling projects still have this problem. `CuteDB/.github/workflows/` and
> `FAISS.Net/.github/workflows/` are nested and therefore inert. Moving them is the same one-line
> `git mv`, but it will start running their CI — worth doing deliberately rather than in passing.

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
fires on Windows fires readily on Linux, and the concurrency tests exist to catch that class of bug.

**Why the sample only builds on Windows.** Avalonia's headless renderer needs Skia's native
dependencies; installing them on three runners to compile one sample is not worth the minutes.

**Why the clients run against a live server.** The only thing worth testing in a protocol client is
that its bytes match what the server actually sends back, and a mock cannot tell you that. The job
starts `memsharp serve`, polls the port until it answers, then runs all three suites against it.

**Why pack runs on every commit.** A packaging mistake — a missing readme, a broken license
expression, a project that should not have been packable — is cheap to find here and expensive to
find at release time, when NuGet versions are already immutable.

**Two traps this file already works around:**

- The test step deliberately omits `--nologo`. The SDK forwards unrecognised arguments through to
  the test runner, and Microsoft.Testing.Platform — which xunit.v3 uses — rejects it with "Unknown
  option" and reports *zero tests ran* rather than failing loudly. A green-looking run with a total
  of 0 is this.
- `setup-go` runs with `cache: false`. The Go client has no dependencies, so there is no `go.sum`,
  and `cache-dependency-path` pointing at a file that does not exist fails the step outright.

## memsharp-release.yml

Triggered by a tag matching `memsharp-v*`.

```bash
# from the repository root, after updating <Version> in MemSharp/Directory.Build.props
git tag memsharp-v1.0.1
git push origin memsharp-v1.0.1
```

Tags are namespaced rather than bare `v1.0.1` because this is a monorepo: a bare version tag would
be ambiguous about which project it releases, and would fire every project's release workflow at
once.

The workflow resolves the version from the tag, **fails if it disagrees with
`Directory.Build.props`**, then tests, packs, pushes to nuget.org and creates a GitHub release. The
version guard matters more than it looks: a package whose tag claims a different version cannot be
corrected afterwards, because published NuGet versions are immutable.

`workflow_dispatch` runs the same pipeline with `dry_run` defaulted to true, which builds and packs
without publishing — the way to rehearse a release.

> **1.0.0 is already published** to nuget.org, PyPI and npm. Those versions are immutable, so a
> release run for `memsharp-v1.0.0` would fail on the duplicate. The next release needs a version
> bump in `MemSharp/Directory.Build.props` (both NuGet packages),
> `MemSharp/clients/python/pyproject.toml` and `MemSharp/clients/nodejs/package.json`.

### Required setup

| | |
|---|---|
| Secret `NUGET_API_KEY` | An API key from nuget.org, glob-scoped to `MemSharp*` so it covers both packages and nothing else |
| Environment `nuget` | Create under **Settings → Environments** with required reviewers. Publishing then needs a human approval even though pushing a tag does not |

Both packages are published together: `MemSharp` and `MemSharp.Cli`. The README tells users to
install the CLI, so shipping only the library would leave that instruction broken.

The release workflow does **not** publish the Python or npm clients — those were pushed by hand, and
automating them would mean putting a PyPI and an npm token in the repository's secrets too. Worth
adding when the release cadence justifies it.

---

## Running the same checks locally

```bash
cd MemSharp

dotnet build -c Release
dotnet test tests/MemSharp.Tests/MemSharp.Tests.csproj -c Release
dotnet pack  -c Release                       # packages land in artifacts/packages/
dotnet run   -c Release --project samples/MemSharp.TradingDemo -- --capture docs/images
python .github/scripts/check_docs.py .

# clients, against a server you start yourself
dotnet run -c Release --project src/MemSharp.Cli -- serve --port 6391 --quiet &
python clients/python/test_client.py
node clients/nodejs/test/client.test.js
(cd clients/go && go test ./...)
```
