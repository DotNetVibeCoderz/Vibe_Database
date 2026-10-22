# CuteDB.Browser

**CuteDB Browser**, a desktop workbench for [CuteDB](https://www.nuget.org/packages/CuteDB) — the
cute embedded document database for .NET.

Built by [Gravicode Studios](https://github.com/DotNetVibeCoderz), led by Kang Fadhil.

```bash
dotnet tool install -g CuteDB.Browser
cutebrowser
```

![The workbench](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Database/main/CuteDB/docs/images/browser/01-workbench.png)

## What it does

- **Query tabs** in CuteQL or C# LINQ, with line numbers, highlighting, Run (`F5`), Check (`F7`)
  and a Format that is a real round trip through the parser.
- **The plan band** under every query: rows examined, matched and returned, the duration, and
  whether the native accelerator ran the scan — so a missing index is visible, not guessed at.
- **LINQ tabs** are C# scripts with the open database in scope; return an `IQueryable` and the
  CuteQL it translated to is printed above the rows.
- **The explorer** shows collections, their fields inferred from a sample of documents, and their
  indexes.
- **Jack — The Code Bender**, an assistant that lists collections and describes their real fields
  before writing a query, validates what he writes, and never runs a write himself. OpenAI, Azure
  OpenAI, Claude, Gemini, Ollama, or anything that speaks OpenAI's API.
- **Templates** for new databases and new queries, all runnable against the Retail template.

## Settings

`Tools ▸ Settings` writes to `CuteDB/Browser/app.config` in your application-data folder
(`%APPDATA%` on Windows, `~/.config` elsewhere), so `dotnet tool update -g CuteDB.Browser` keeps
them. API keys can be left blank and supplied through `OPENAI_API_KEY`, `AZURE_OPENAI_API_KEY`,
`ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, `OPENAI_COMPATIBLE_API_KEY` and `TAVILY_API_KEY` instead.

## Without a window

```bash
cutebrowser --help
cutebrowser --ask "Which city brought in the most revenue?"
cutebrowser --ask "How many orders?" --db shop.cute
```

`--ask` runs one assistant turn through the same agent and tools the panel uses and prints every
tool call.

On Linux, Avalonia needs the usual X11 libraries (`libx11`, `libice`, `libsm`, `fontconfig`). For a
machine with no .NET at all, self-contained archives for Windows, Linux and macOS are attached to
each [GitHub release](https://github.com/DotNetVibeCoderz/Vibe_Database/releases).

## Links

- [CuteDB Browser guide](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/browser.md)
  — also in [Bahasa Indonesia](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/id/browser.md)
- [CuteQL reference](https://github.com/DotNetVibeCoderz/Vibe_Database/blob/main/CuteDB/docs/en/cuteql.md)
- [Source](https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB/tools/CuteBrowser)

MIT licensed.
