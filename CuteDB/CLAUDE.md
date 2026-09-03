# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

CuteDB 2.x is an embedded document database for .NET 10, inside the larger `Vibe_Database` monorepo
(siblings: `FAISS.Net`, `MemSharp` — independent projects, leave them alone). Built by Gravicode
Studios, led by Kang Fadhil.

It ships as three NuGet packages (`CuteDB`, `CuteDB.Cli`, `CuteDB.Server`), a Rust accelerator, and
client libraries for Python, Go and Node.js.

```
src/CuteDB          the engine — no package dependencies, on purpose
src/CuteDB.Cli      `cutedb` dotnet tool (Spectre.Console)
src/CuteDB.Server   HTTP API, so the non-.NET clients have something to talk to
native/cutedb-core  Rust scan accelerator, C ABI
clients/{python,go,nodejs}
samples/CuteDB.Retail  shared sample dataset, referenced by CLI, demo and benchmarks
samples/CuteDB.Demo    Avalonia demo, also renders the docs screenshots
tests/CuteDB.Tests     154 tests
benchmarks/            BenchmarkDotNet; source of every number in the docs
```

## Commands

```bash
dotnet build CuteDB.slnx
dotnet test tests/CuteDB.Tests                      # 154 tests
dotnet test tests/CuteDB.Tests --filter "FullyQualifiedName~NativeParityTests"

pwsh native/build.ps1                               # or ./native/build.sh — optional
cd native && cargo test --release && cargo clippy --all-targets -- -D warnings

dotnet run --project samples/CuteDB.Demo                                    # the demo
dotnet run --project samples/CuteDB.Demo -- --screenshot docs/images        # regenerate screenshots
dotnet run -c Release --project benchmarks/CuteDB.Benchmarks -- --filter '*Scan*'

dotnet run --project src/CuteDB.Cli -- seed /tmp/x.cute --scale demo
```

**Run `native/build.ps1` before the tests if you touched the Rust.** The .NET build never depends on
Rust, but `NativeParityTests` has a canary that fails when the library did not load — without it the
other 35 parity cases would compare managed against managed and prove nothing.

**Verify both paths after touching the engine.** `CUTEDB_DISABLE_NATIVE=1 dotnet test` forces the
managed evaluator; the fallback is a documented promise, not a convenience.

## Architecture

Read `docs/en/architecture.md` first — it is written for exactly this purpose and is kept current.
The short version, and the invariants that are easy to break:

**The binary format is the whole design.** Every container carries a `u32` payload length *before*
its contents, so a reader skips a subtree it does not want with one read and an addition. Reading
one nested field out of a stored document is 155 ns / 32 B; decoding the document first is 10,305 ns
/ 11,592 B. Any change that makes the scan path decode whole documents throws away the project's
reason to exist.

**Tag numbers in `CuteType` are on-disk constants** and are hard-coded again in
`native/cutedb-core/src/value.rs`. Append; never renumber. Bump `CuteFileFormat.Version` if old
files could not be read.

**Documents live in unmanaged slabs**, addressed by a flat `DocRef[]`. `DocRef`'s layout is shared
with Rust — three `u32`s, in that order. `SlabAllocator` uses `NativeMemory.AlignedAlloc`, so it must
be freed with `AlignedFree`.

**The managed and native scanners must agree exactly.** `CuteValueComparer` and
`CuteEvaluator.Compare` in C# mirror `native/cutedb-core/src/compare.rs`. Changing semantics on one
side without the other is a correctness bug the parity suite is there to catch. Where exact
agreement is unaffordable — a stored decimal against a double — Rust returns
`CompareOutcome::Unsupported` and the scan falls back mid-flight rather than guessing.

**Three query semantics that are deliberate, not accidents:**

- A field holding an array compares element-wise against a scalar (`tags = 'promo'` means "contains
  promo"). Without this, an index over an array field returns exactly the right candidates and then
  the re-check rejects all of them. `IN` uses the same comparison for the same reason.
- `MISSING` is distinct from `NULL`, and comparing against either yields *unknown*, so a row appears
  under neither `x > 0` nor `NOT (x > 0)`.
- Grouped projections resolve group keys by the *source text* of the grouping expression, because
  after grouping there is no document left to resolve `address.city` against.

## Conventions

- **Bilingual, everywhere user-visible.** Every doc page exists in `docs/en/` and `docs/id/`; the
  README has `README.md` and `README.id.md`. The demo and CLI mix Indonesian and English. Update both
  halves.
- **Numbers in docs come from `benchmarks/`.** If you quote a figure, it should be reproducible by
  the command named next to it. `cutedb bench` is explicitly *not* the source and says so.
- **Screenshots are generated, never hand-made.** `--screenshot docs/images` renders them from the
  real window with the real dataset, so they cannot drift.
- The core library takes **no package dependencies**. CRC-32C is hand-rolled to keep that true.
- The demo builds its views in C# rather than XAML (see `Views/Ui.cs` for why); `MainWindow` is the
  one XAML file. Do not add a hand-written `InitializeComponent` — that suppresses the generated one
  and leaves every `x:Name` field null.
- XAML comments cannot contain `--`.

## Gotchas that have already cost time

- **Assembly names are case-insensitive on Windows and macOS.** The CLI assembly is `CuteDB.Cli`, not
  `cutedb`, because the latter collides with `CuteDB.dll` in one output directory. The command name
  comes from `ToolCommandName`.
- **`Application.Resources` must precede `Application.Styles`** in `App.axaml`: a `StaticResource`
  inside a style is resolved as the styles load.
- **`FindResource` returns null before a control is in the visual tree.** The demo's views build
  charts in their constructors, so `Ui.Brush` goes to `Application.Current` — otherwise every chart
  silently renders black.
- **Nothing on the Rust hot path may allocate.** A `Vec` per document made the accelerator *slower*
  than the managed scanner it exists to beat. The VM's operand stack is a fixed array; `LIKE` runs
  over UTF-8 bytes for ASCII.
- Avalonia is pinned to 11.3.13 because `Avalonia.Controls.DataGrid` has no later 11.3.x release.
  Spectre is pinned to 0.55.0 because `Spectre.Console.Cli` stops there.
