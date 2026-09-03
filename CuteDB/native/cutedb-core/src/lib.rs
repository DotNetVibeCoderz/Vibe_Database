//! `cutedb_core` — the native scan accelerator for CuteDB.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.
//! <https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB>
//!
//! # What this is for
//!
//! CuteDB keeps every document in unmanaged slabs, encoded in a self-describing binary format, and
//! addressed by a flat table of `(slab, offset, length)` triples. That layout exists so that a
//! filtering scan can be handed across the FFI boundary *once*: the slab addresses, the slot table
//! and a compiled predicate go over, and a list of matching row numbers comes back. Nothing is
//! copied and nothing is pinned, because the memory is already outside the GC's world.
//!
//! Everything here is an optimisation. The managed library implements the same semantics and is
//! used whenever this library is absent, whenever a predicate uses something the bytecode does not
//! cover, or whenever a comparison turns out to be one this crate refuses to guess at. That is why
//! every entry point reports failure as a status code the caller can shrug off, rather than as a
//! panic.
//!
//! # Safety
//!
//! The two `unsafe` obligations the caller carries are stated on [`cutedb_scan`]. Both are
//! satisfied by construction on the managed side: the slabs outlive the call because the collection
//! holds them, and the slot table is a `fixed` array for the duration of the P/Invoke.

use std::panic::{catch_unwind, AssertUnwindSafe};

pub mod compare;
pub mod path;
pub mod value;
pub mod vm;

#[cfg(test)]
mod tests;

/// Where one encoded document lives. Must match `DocRef` in `SlabAllocator.cs` exactly.
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct DocRef {
    pub slab: u32,
    pub offset: u32,
    pub length: u32,
}

/// The bytecode ABI this library speaks. The managed side refuses to load a mismatched build.
#[no_mangle]
pub extern "C" fn cutedb_abi_version() -> u32 {
    vm::ABI_VERSION
}

/// A NUL-terminated version string owned by this library.
#[no_mangle]
pub extern "C" fn cutedb_version_string() -> *const std::os::raw::c_char {
    concat!(env!("CARGO_PKG_VERSION"), "\0").as_ptr() as *const std::os::raw::c_char
}

/// A fast 64-bit hash, for the places CuteDB wants a digest rather than a cryptographic one.
///
/// # Safety
///
/// `data` must point to at least `length` readable bytes, or be null when `length` is zero.
#[no_mangle]
pub unsafe extern "C" fn cutedb_hash64(data: *const u8, length: usize) -> u64 {
    if data.is_null() || length == 0 {
        return FNV_OFFSET;
    }

    let bytes = std::slice::from_raw_parts(data, length);
    hash64(bytes)
}

/// Runs a compiled predicate over a slot table, writing matching row numbers into `out_rows`.
///
/// Returns 0 on success. A negative value means the scan did not complete and the caller should
/// evaluate the query in managed code instead; `out_count` is not meaningful in that case.
///
/// # Safety
///
/// The caller must guarantee, for the duration of the call:
///
/// * `slabs` points to `slab_count` readable pointers, each of which is the base of a slab that
///   contains every byte range any [`DocRef`] naming it refers to.
/// * `refs` points to `ref_count` readable [`DocRef`] values, `program` to `program_len` readable
///   bytes, and `out_rows` to `out_capacity` writable `u32`s.
///
/// A [`DocRef`] whose `length` is zero is a hole in the slot table and is skipped without being
/// dereferenced, so holes need no valid slab.
#[no_mangle]
pub unsafe extern "C" fn cutedb_scan(
    slabs: *const *const u8,
    slab_count: usize,
    refs: *const DocRef,
    ref_count: usize,
    program: *const u8,
    program_len: usize,
    out_rows: *mut u32,
    out_capacity: usize,
    out_count: *mut usize,
) -> i32 {
    if slabs.is_null()
        || refs.is_null()
        || program.is_null()
        || out_rows.is_null()
        || out_count.is_null()
    {
        return -100;
    }

    // A panic must never cross back into the runtime: it is undefined behaviour there, and the
    // managed side is perfectly able to answer the query itself. Anything that escapes becomes a
    // status code.
    let result = catch_unwind(AssertUnwindSafe(|| {
        let slab_pointers = std::slice::from_raw_parts(slabs, slab_count);
        let slot_table = std::slice::from_raw_parts(refs, ref_count);
        let program_bytes = std::slice::from_raw_parts(program, program_len);
        let output = std::slice::from_raw_parts_mut(out_rows, out_capacity);

        let compiled = match vm::Program::parse(program_bytes) {
            Ok(compiled) => compiled,
            Err(error) => return Err(error),
        };

        let mut matched = 0usize;
        for (row, slot) in slot_table.iter().enumerate() {
            // Holes left by deletes carry a zero length and are skipped without a dereference.
            if slot.length == 0 {
                continue;
            }

            let slab = match slab_pointers.get(slot.slab as usize) {
                Some(base) if !base.is_null() => *base,
                _ => return Err(vm::VmError::BadDocument),
            };

            let document =
                std::slice::from_raw_parts(slab.add(slot.offset as usize), slot.length as usize);

            match compiled.test(document) {
                Ok(true) => {
                    if matched < output.len() {
                        output[matched] = row as u32;
                    }

                    matched += 1;

                    // The caller sized the buffer for every row that could match; stopping here
                    // rather than writing past it keeps a LIMIT honest without a partial answer.
                    if matched >= output.len() {
                        break;
                    }
                }
                Ok(false) => {}
                Err(error) => return Err(error),
            }
        }

        Ok(matched)
    }));

    match result {
        Ok(Ok(matched)) => {
            *out_count = matched;
            0
        }
        Ok(Err(error)) => error.code(),
        Err(_) => -200,
    }
}

const FNV_OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;

/// FNV-1a, widened to eight bytes at a time.
///
/// Not the fastest hash in the world, but it is short, dependency-free, and the managed side only
/// uses it where a digest of a byte range is wanted rather than a hash table's hash.
fn hash64(bytes: &[u8]) -> u64 {
    let mut hash = FNV_OFFSET;

    let mut chunks = bytes.chunks_exact(8);
    for chunk in &mut chunks {
        let word = u64::from_le_bytes([
            chunk[0], chunk[1], chunk[2], chunk[3], chunk[4], chunk[5], chunk[6], chunk[7],
        ]);
        hash ^= word;
        hash = hash.wrapping_mul(FNV_PRIME);
    }

    for &byte in chunks.remainder() {
        hash ^= byte as u64;
        hash = hash.wrapping_mul(FNV_PRIME);
    }

    hash
}
