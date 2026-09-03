# CuteDB documentation

Built by Gravicode Studios, led by Kang Fadhil.

Everything is written twice, in English and in Bahasa Indonesia. The two are kept in step; if they
ever disagree, the English page is the one that was edited first.

| | English | Bahasa Indonesia |
| --- | --- | --- |
| **Getting started** — install, first database, first query | [en/getting-started.md](en/getting-started.md) | [id/memulai.md](id/memulai.md) |
| **CuteQL** — the query language, in full | [en/cuteql.md](en/cuteql.md) | [id/cuteql.md](id/cuteql.md) |
| **Architecture** — how it works inside, and why | [en/architecture.md](en/architecture.md) | [id/arsitektur.md](id/arsitektur.md) |
| **Performance** — measurements, method, and the losses | [en/performance.md](en/performance.md) | [id/performa.md](id/performa.md) |
| **Command line** — every `cutedb` command | [en/cli.md](en/cli.md) | [id/cli.md](id/cli.md) |
| **Server & clients** — HTTP API, Python, Go, Node.js | [en/server-and-clients.md](en/server-and-clients.md) | [id/server-dan-klien.md](id/server-dan-klien.md) |
| **File format** — the bytes on disk | [en/file-format.md](en/file-format.md) | [id/format-berkas.md](id/format-berkas.md) |

## Where to start

- **Evaluating CuteDB?** Read the [README](../README.md), then run the demo:
  `dotnet run --project samples/CuteDB.Demo`. It shows the engine's behaviour, not just its output.
- **Ready to use it?** [Getting started](en/getting-started.md) is twenty minutes end to end.
- **Wondering whether it will be fast enough?** [Performance](en/performance.md) has the numbers
  and, more usefully, the cases where CuteDB is the wrong choice.
- **Curious how it works?** [Architecture](en/architecture.md) explains the binary document format
  and why reading one field out of a stored document costs 155 nanoseconds.

## Screenshots

The images in `images/` are rendered from the real demo application by
`dotnet run --project samples/CuteDB.Demo -- --screenshot docs/images`, so they cannot drift from
the app they show.
