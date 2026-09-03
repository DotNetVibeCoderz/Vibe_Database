# The file format

*[Bahasa Indonesia →](../id/format-berkas.md)*

Written down so a `.cute` file can be read by something that is not CuteDB, and so the Rust
accelerator and the C# engine have one specification to agree with rather than each other.

Everything is little-endian **except** `CuteId`, which is big-endian by its own definition.
Format version 2.

## The file

```
┌────────────────────────────┐
│ header            64 bytes │
├────────────────────────────┤
│ frame                      │
│ frame                      │   append-only, in the order the writes happened
│ …                          │
└────────────────────────────┘
```

### Header

| Offset | Size | |
| ---: | ---: | --- |
| 0 | 8 | magic — `43 55 54 45 44 42 00 00` (`CUTEDB\0\0`) |
| 8 | 4 | format version, currently `2` |
| 12 | 4 | flags, currently 0 |
| 16 | 8 | creation time, .NET UTC ticks |
| 24 | 40 | reserved, zero |

### Frame

| Offset | Size | |
| ---: | ---: | --- |
| 0 | 1 | opcode |
| 1 | 1 | reserved, zero |
| 2 | 2 | collection id |
| 4 | 4 | payload length |
| 8 | 4 | CRC-32C (Castagnoli, `0x1EDC6F41`) of the payload |
| 12 | *n* | payload |

Maximum payload is 16 MiB, which is therefore the largest single document.

| Opcode | | Payload |
| ---: | --- | --- |
| 1 | Upsert | 12-byte id, then the encoded document |
| 2 | Delete | 12-byte id |
| 3 | DefineCollection | varint-prefixed UTF-8 name |
| 4 | DropCollection | empty |
| 5 | DefineIndex | unique flag (1 byte), name, path — both varint-prefixed UTF-8 |
| 6 | DropIndex | varint-prefixed UTF-8 name |
| 7 | Checkpoint | empty; written on a clean close |

### Reading one

Replay from offset 64. For each frame, read the 12-byte header, read the payload, check the CRC.
**Stop at the first frame whose length is implausible or whose checksum does not match** — that is
the one that was being written when the process died, and everything after it is suspect too.
Everything before it is intact by construction.

Later frames supersede earlier ones for the same id. A collection's current contents are the last
`Upsert` per id, minus anything a later `Delete` removed.

## Document encoding

A value is a one-byte tag and a payload.

| Tag | Type | Payload |
| ---: | --- | --- |
| `00` | Null | — |
| `01` | False | — |
| `02` | True | — |
| `03` | Int32 | 4 bytes |
| `04` | Int64 | 8 bytes |
| `05` | Double | 8 bytes, IEEE-754 |
| `06` | String | varint byte length, then UTF-8 |
| `07` | Binary | varint byte length, then bytes |
| `08` | Array | `u32` payload length, varint count, then values |
| `09` | Object | `u32` payload length, varint count, then entries |
| `0A` | DateTime | 8 bytes, .NET UTC ticks |
| `0B` | Guid | 16 bytes |
| `0C` | Decimal | 16 bytes — see below |
| `0D` | Id | 12 bytes |

An object entry is a varint key length, the key as UTF-8, then a value.

Varints are unsigned LEB128, at most five bytes.

The `u32` length on a container counts the bytes *after* the length field, so skipping a subtree is
one read and an addition. That single property is what makes reading one field out of a stored
document cost 155 nanoseconds instead of ten microseconds — see
[architecture](architecture.md).

### Decimal

.NET's `decimal` is a 96-bit unsigned mantissa with a sign and a scale of 0–28. `decimal.GetBits`
returns four `int`s and they are packed as:

```
lo = bits[1] << 32 | bits[0]        // mantissa, low 64 bits
hi = bits[3] << 32 | bits[2]        // flags in the top 32, mantissa high 32 in the low 32
```

So mantissa is `(hi & 0xFFFFFFFF) << 64 | lo`, scale is `(hi >> 48) & 0xFF`, and the value is
negative when bit 63 of `hi` is set.

### CuteId

Twelve bytes, **big-endian**: 4 bytes of Unix seconds, 5 bytes of per-process random, 3 bytes of
counter. Big-endian so that raw byte order matches value order, which makes a range index over ids
also a range index over creation time.

The text form is 24 lowercase hex characters.

## Worked example

```json
{ "n": 7, "city": "Bandung" }
```

```
09                          Object
1B 00 00 00                 payload length = 27
02                          2 fields
  01 6E                       key length 1, "n"
  03 07 00 00 00              Int32 7
  04 63 69 74 79              key length 4, "city"
  06 07 42 61 6E 64 75 6E 67  String length 7, "Bandung"
```

Total 33 bytes. Finding `city` reads the first key, sees `n`, reads the Int32's fixed width, adds 5,
and lands on the next key — without looking at the value it skipped.

## Compatibility

The version in the header is checked on open, and a file written by a different version is refused
rather than misread. Tag numbers are on-disk constants: new ones may be appended, existing ones are
never renumbered.

Version 1's `.jdb` files are unrelated — Newtonsoft JSON with `TypeNameHandling.All`, which tied
them to assembly names. They are not read. Export from the old version and import the JSON.

## Reading a file without CuteDB

Everything needed is above; the format is self-describing and position-independent. The two
reference implementations are worth reading if you write a third:

- [`src/CuteDB/Serialization/CuteBinary.cs`](../../src/CuteDB/Serialization/CuteBinary.cs) — C#
- [`native/cutedb-core/src/value.rs`](../../native/cutedb-core/src/value.rs) — Rust

The Rust one is about 300 lines including the skip-and-seek logic, which is a fair estimate of what
a reader costs.
