//! The native half of the Rust experiment. Each export is the smallest thing that answers one
//! question about putting Rust under MemSharp; see ../README.md for the questions and the answers.

use std::collections::HashMap;
use std::sync::Mutex;

/// Does nothing. Measures the bare cost of a .NET -> native call.
#[no_mangle]
pub extern "C" fn ms_noop() -> i32 {
    0
}

/// FNV-1a over a buffer - the snapshot checksum MemSharp uses, byte for byte.
///
/// # Safety
/// `data` must point to `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn ms_fnv1a(data: *const u8, len: usize) -> u64 {
    let bytes = std::slice::from_raw_parts(data, len);
    let mut hash: u64 = 14695981039346656037;
    for &b in bytes {
        hash ^= b as u64;
        hash = hash.wrapping_mul(1099511628211);
    }
    hash
}

/// XXH3-64 over a buffer - a hash built for wide registers, unlike FNV's one-byte dependency chain.
///
/// # Safety
/// `data` must point to `len` readable bytes.
#[no_mangle]
pub unsafe extern "C" fn ms_xxh3(data: *const u8, len: usize) -> u64 {
    xxhash_rust::xxh3::xxh3_64(std::slice::from_raw_parts(data, len))
}

// A string map with the same contract as MemDb's string GET/SET: UTF-16 in, UTF-16 out, behind a
// lock. One mutex rather than shards, because the benchmark is single-threaded - it measures the
// boundary and the copy, not contention.
fn store() -> &'static Mutex<HashMap<Vec<u16>, Vec<u16>>> {
    static STORE: std::sync::OnceLock<Mutex<HashMap<Vec<u16>, Vec<u16>>>> = std::sync::OnceLock::new();
    STORE.get_or_init(|| Mutex::new(HashMap::new()))
}

/// # Safety
/// `key` and `value` must point to `key_len` and `value_len` UTF-16 code units.
#[no_mangle]
pub unsafe extern "C" fn ms_set(key: *const u16, key_len: usize, value: *const u16, value_len: usize) {
    let key = std::slice::from_raw_parts(key, key_len).to_vec();
    let value = std::slice::from_raw_parts(value, value_len).to_vec();
    store().lock().unwrap().insert(key, value);
}

/// Copies the value into `out` and returns its length, -1 if absent, or the required length
/// negated minus 2 if `out` is too small.
///
/// # Safety
/// `key` must point to `key_len` code units and `out` to `capacity` writable ones.
#[no_mangle]
pub unsafe extern "C" fn ms_get(key: *const u16, key_len: usize, out: *mut u16, capacity: usize) -> isize {
    let key = std::slice::from_raw_parts(key, key_len);
    let map = store().lock().unwrap();
    match map.get(key) {
        None => -1,
        Some(value) if value.len() > capacity => -(value.len() as isize) - 2,
        Some(value) => {
            std::ptr::copy_nonoverlapping(value.as_ptr(), out, value.len());
            value.len() as isize
        }
    }
}
