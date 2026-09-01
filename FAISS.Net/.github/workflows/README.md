# CI workflows

> **These files must be moved to the repository root before they will run.**
>
> GitHub Actions only reads `.github/workflows/` at the root of a repository. FAISS.Net lives at
> `Vibe_Database/FAISS.Net/`, so a workflow left in `FAISS.Net/.github/workflows/` is silently
> ignored — no error, no run, nothing.

They are written for the monorepo layout already: path filters scope them to `FAISS.Net/**`, and
every job sets `working-directory: FAISS.Net`. Only their location needs to change.

```bash
# from the root of the Vibe_Database checkout
mkdir -p .github/workflows .github/scripts
cp FAISS.Net/.github/workflows/faissnet-ci.yml      .github/workflows/
cp FAISS.Net/.github/workflows/faissnet-release.yml .github/workflows/
```

The docs check stays where it is — `faissnet-ci.yml` invokes it at
`FAISS.Net/.github/scripts/check_docs.py`, which is a normal repository path and needs no move.

Names are prefixed `faissnet-` so they do not collide with other projects' workflows in the same
repository.

---

## faissnet-ci.yml

Runs on pushes and pull requests that touch `FAISS.Net/**`.

| Job | What it does |
|---|---|
| `test` | Restores, builds with `-warnaserror`, and runs the 82 tests on **ubuntu, windows and macos** |
| `pack` | Builds both NuGet packages and uploads them as artifacts |
| `docs` | Verifies every relative link resolves and that `docs/en` and `docs/id` are still in step |

**Why three operating systems.** `VectorOps` dispatches on the widest SIMD register the hardware
reports, so the runners exercise genuinely different code paths — AVX2 or AVX-512 on the x64
runners, NEON on the arm64 macOS runner. A distance kernel that is correct on one is not thereby
correct on the others, and the dimension-sweep tests exist precisely to catch tail-handling bugs at
each register width. `fail-fast` is off so a failure on one platform still reports the others.

**Why pack runs on every commit.** A packaging mistake — a missing readme, a broken license
expression, a project that should not have been packable — is cheap to find here and expensive to
find at release time, when NuGet versions are already immutable.

## faissnet-release.yml

Triggered by a tag matching `faissnet-v*`.

```bash
# from the repository root, after updating <Version> in FAISS.Net/Directory.Build.props
git tag faissnet-v1.0.0
git push origin faissnet-v1.0.0
```

Tags are namespaced rather than bare `v1.0.0` because this is a monorepo: a bare version tag would
be ambiguous about which project it releases, and would fire every project's release workflow at
once.

The workflow resolves the version from the tag, **fails if it disagrees with
`Directory.Build.props`**, then builds, tests, packs, pushes to nuget.org and creates a GitHub
release. The version guard matters more than it looks: a package whose repository claims a different
version cannot be corrected afterwards, because published NuGet versions are immutable.

`workflow_dispatch` runs the same pipeline with `dry_run` defaulted to true, which builds and packs
without publishing — the way to rehearse a release.

### Required setup

| | |
|---|---|
| Secret `NUGET_API_KEY` | An API key from nuget.org, glob-scoped to `Gravicode.FaissNet*` so it covers both packages and nothing else |
| Environment `nuget` | Create under **Settings → Environments** with required reviewers. Publishing then needs a human approval even though pushing a tag does not |

Both packages are published together: `Gravicode.FaissNet` and `Gravicode.FaissNet.Gpu`. The README instructs users to
install the GPU package, so shipping only the core one would leave that instruction broken.

---

## Running the same checks locally

```bash
dotnet build -c Release -warnaserror
dotnet test  -c Release
dotnet pack  -c Release                       # packages land in artifacts/packages/
python .github/scripts/check_docs.py .
```
