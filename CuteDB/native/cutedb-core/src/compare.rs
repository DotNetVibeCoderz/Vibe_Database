//! Comparison semantics, mirroring `CuteValueComparer` and `CuteEvaluator` on the managed side.
//!
//! Every rule here has a counterpart in C#, and the parity tests in
//! `tests/CuteDB.Tests/NativeParityTests.cs` run both over the same data and demand identical
//! answers. When the two cannot be made to agree cheaply — the one case being a decimal compared
//! against a double, where .NET's conversion rounds differently from anything Rust does in one
//! step — this module reports [`CompareOutcome::Unsupported`] and the scan hands the query back
//! to the managed evaluator rather than guessing.
//!
//! Nothing on the hot path allocates. That is not incidental: a scan runs these functions once per
//! document, and a single heap allocation per row is enough to make the whole accelerator slower
//! than the managed code it exists to beat.

use crate::value::{self, Value};

/// The result of comparing two values.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompareOutcome {
    /// A definite ordering.
    Ordered(core::cmp::Ordering),
    /// No answer is meaningful — a missing operand, or ordering against null.
    Unknown,
    /// This build cannot reproduce the managed result exactly; fall back.
    Unsupported,
}

/// The six comparison operators.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompareOp {
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

impl CompareOp {
    #[inline]
    fn satisfied_by(self, ordering: core::cmp::Ordering) -> bool {
        use core::cmp::Ordering::*;
        match self {
            CompareOp::Equal => ordering == Equal,
            CompareOp::NotEqual => ordering != Equal,
            CompareOp::Less => ordering == Less,
            CompareOp::LessOrEqual => ordering != Greater,
            CompareOp::Greater => ordering == Greater,
            CompareOp::GreaterOrEqual => ordering != Less,
        }
    }

    #[inline]
    fn is_equality(self) -> bool {
        matches!(self, CompareOp::Equal | CompareOp::NotEqual)
    }
}

/// The outcome of a predicate test: true, false, or "unknown", which rejects the row.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Truth {
    True,
    False,
    Unknown,
    Unsupported,
}

impl Truth {
    #[inline]
    pub fn of(value: bool) -> Truth {
        if value {
            Truth::True
        } else {
            Truth::False
        }
    }

    #[inline]
    pub fn is_true(self) -> bool {
        self == Truth::True
    }
}

/// Applies a comparison operator, with the same three-valued logic the managed evaluator uses.
pub fn compare_op(op: CompareOp, left: &Value, right: &Value) -> Truth {
    // Comparing against an absent field has no true answer.
    if left.is_missing() || right.is_missing() {
        return Truth::Unknown;
    }

    if left.is_null() || right.is_null() {
        if !op.is_equality() {
            return Truth::Unknown;
        }

        let both_null = left.is_null() && right.is_null();
        return Truth::of(if op == CompareOp::Equal {
            both_null
        } else {
            !both_null
        });
    }

    // A field holding an array, compared against a scalar, matches when any element does. This is
    // what makes an index over an array field usable, and it is the behaviour
    // `CuteEvaluator.Compare` implements.
    if left.is_array() != right.is_array() {
        return compare_array_against_scalar(op, left, right);
    }

    // Equality between two strings is byte equality whatever the encoding, and it is by far the
    // most common comparison a scan performs. Answering it here skips the code-unit decoding that
    // ordering needs.
    if op.is_equality() {
        if let (Value::Str(a), Value::Str(b)) = (left, right) {
            return Truth::of((a == b) == (op == CompareOp::Equal));
        }
    }

    match compare(left, right) {
        CompareOutcome::Ordered(ordering) => Truth::of(op.satisfied_by(ordering)),
        CompareOutcome::Unknown => Truth::Unknown,
        CompareOutcome::Unsupported => Truth::Unsupported,
    }
}

fn compare_array_against_scalar(op: CompareOp, left: &Value, right: &Value) -> Truth {
    let (raw, scalar, array_on_left) = match (left, right) {
        (Value::Array(raw), scalar) => (*raw, *scalar, true),
        (scalar, Value::Array(raw)) => (*raw, *scalar, false),
        _ => return Truth::Unknown,
    };

    let iterator = match value::ArrayIter::new(raw) {
        Ok(iterator) => iterator,
        Err(_) => return Truth::Unsupported,
    };

    // NOT EQUAL over an array means "no element equals", not "some element differs".
    let looking_for_equality = op != CompareOp::NotEqual;

    for element in iterator {
        let element = match element.and_then(value::decode) {
            Ok(element) => element,
            Err(_) => return Truth::Unsupported,
        };

        let outcome = if array_on_left {
            compare(&element, &scalar)
        } else {
            compare(&scalar, &element)
        };

        let ordering = match outcome {
            CompareOutcome::Ordered(ordering) => ordering,
            CompareOutcome::Unknown => continue,
            CompareOutcome::Unsupported => return Truth::Unsupported,
        };

        if looking_for_equality {
            if op.satisfied_by(ordering) {
                return Truth::True;
            }
        } else if ordering == core::cmp::Ordering::Equal {
            return Truth::False;
        }
    }

    Truth::of(!looking_for_equality)
}

/// Total ordering over two values, matching `CuteValueComparer.Compare`.
pub fn compare(left: &Value, right: &Value) -> CompareOutcome {
    use core::cmp::Ordering;

    let left_rank = left.type_rank();
    let right_rank = right.type_rank();
    if left_rank != right_rank {
        return CompareOutcome::Ordered(left_rank.cmp(&right_rank));
    }

    let ordering = match (left, right) {
        (Value::Missing, _) | (Value::Null, _) => Ordering::Equal,
        (Value::Bool(a), Value::Bool(b)) => a.cmp(b),
        (Value::Str(a), Value::Str(b)) => {
            return CompareOutcome::Ordered(compare_utf8_ordinal(a, b))
        }
        (Value::Binary(a), Value::Binary(b)) => a.cmp(b),
        (Value::DateTime(a), Value::DateTime(b)) => a.cmp(b),
        (Value::Guid(a_lo, a_hi), Value::Guid(b_lo, b_hi)) => {
            return CompareOutcome::Ordered(compare_guid(*a_lo, *a_hi, *b_lo, *b_hi))
        }
        (Value::Id(a), Value::Id(b)) => a.cmp(b),
        (Value::Array(a), Value::Array(b)) => return compare_arrays(a, b),

        // Objects need field-name sorting to match the managed order-insensitive comparison. It is
        // rare enough in a scan predicate that handing it back is cheaper than getting it subtly
        // wrong.
        (Value::Object(_), Value::Object(_)) => return CompareOutcome::Unsupported,

        _ => return compare_numbers(left, right),
    };

    CompareOutcome::Ordered(ordering)
}

/// `string.CompareOrdinal` compares UTF-16 code units, which is not the same as comparing UTF-8
/// bytes once anything above the BMP is involved. Comparing decoded code units keeps the native
/// scanner's ordering identical to the managed one for every input.
fn compare_utf8_ordinal(left: &[u8], right: &[u8]) -> core::cmp::Ordering {
    // For ASCII — almost every string in almost every database — a byte comparison and a UTF-16
    // code-unit comparison give the same answer, and `is_ascii` is vectorised.
    if left.is_ascii() && right.is_ascii() {
        return left.cmp(right);
    }

    let mut a = Utf16Units::new(left);
    let mut b = Utf16Units::new(right);

    loop {
        match (a.next(), b.next()) {
            (None, None) => return core::cmp::Ordering::Equal,
            (None, Some(_)) => return core::cmp::Ordering::Less,
            (Some(_), None) => return core::cmp::Ordering::Greater,
            (Some(x), Some(y)) => {
                if x != y {
                    return x.cmp(&y);
                }
            }
        }
    }
}

/// .NET orders GUIDs by `a`, `b`, `c` and then the eight bytes of `d`, not by raw memory order.
fn compare_guid(a_lo: u64, a_hi: u64, b_lo: u64, b_hi: u64) -> core::cmp::Ordering {
    guid_fields(a_lo, a_hi).cmp(&guid_fields(b_lo, b_hi))
}

fn guid_fields(lo: u64, hi: u64) -> (u32, u16, u16, [u8; 8]) {
    let low = lo.to_le_bytes();
    let high = hi.to_le_bytes();

    let a = u32::from_le_bytes([low[0], low[1], low[2], low[3]]);
    let b = u16::from_le_bytes([low[4], low[5]]);
    let c = u16::from_le_bytes([low[6], low[7]]);
    (a, b, c, high)
}

fn compare_arrays(left: &[u8], right: &[u8]) -> CompareOutcome {
    let mut a = match value::ArrayIter::new(left) {
        Ok(iterator) => iterator,
        Err(_) => return CompareOutcome::Unsupported,
    };
    let mut b = match value::ArrayIter::new(right) {
        Ok(iterator) => iterator,
        Err(_) => return CompareOutcome::Unsupported,
    };

    loop {
        match (a.next(), b.next()) {
            (None, None) => return CompareOutcome::Ordered(core::cmp::Ordering::Equal),
            (None, Some(_)) => return CompareOutcome::Ordered(core::cmp::Ordering::Less),
            (Some(_), None) => return CompareOutcome::Ordered(core::cmp::Ordering::Greater),
            (Some(x), Some(y)) => {
                let (x, y) = match (x.and_then(value::decode), y.and_then(value::decode)) {
                    (Ok(x), Ok(y)) => (x, y),
                    _ => return CompareOutcome::Unsupported,
                };

                match compare(&x, &y) {
                    CompareOutcome::Ordered(core::cmp::Ordering::Equal) => continue,
                    other => return other,
                }
            }
        }
    }
}

/// Numeric comparison across all four representations, widening only as far as it must.
fn compare_numbers(left: &Value, right: &Value) -> CompareOutcome {
    let left_integral = matches!(left, Value::I32(_) | Value::I64(_));
    let right_integral = matches!(right, Value::I32(_) | Value::I64(_));

    if left_integral && right_integral {
        return CompareOutcome::Ordered(as_i64(left).cmp(&as_i64(right)));
    }

    let left_decimal = matches!(left, Value::Decimal(_, _));
    let right_decimal = matches!(right, Value::Decimal(_, _));
    let left_double = matches!(left, Value::F64(_));
    let right_double = matches!(right, Value::F64(_));

    // Integers and decimals compare exactly, with no floating point involved.
    if !left_double && !right_double {
        return compare_exact(left, right);
    }

    // A decimal against a double is the one case where matching .NET bit for bit is not
    // affordable: `(double)decimal` rounds through a path that neither `as f64` nor a manual
    // scaling reproduces in every case. Handing this back costs one query the accelerator; getting
    // it wrong would cost correctness.
    if left_decimal || right_decimal {
        return CompareOutcome::Unsupported;
    }

    let a = as_f64(left);
    let b = as_f64(right);

    // NaN is ordered below every other number and equal to itself, matching the managed rule.
    if a.is_nan() || b.is_nan() {
        return CompareOutcome::Ordered(match (a.is_nan(), b.is_nan()) {
            (true, true) => core::cmp::Ordering::Equal,
            (true, false) => core::cmp::Ordering::Less,
            _ => core::cmp::Ordering::Greater,
        });
    }

    CompareOutcome::Ordered(a.partial_cmp(&b).unwrap_or(core::cmp::Ordering::Equal))
}

/// Exact comparison between integers and decimals, with no floating point anywhere.
fn compare_exact(left: &Value, right: &Value) -> CompareOutcome {
    let (a_negative, a_mantissa, a_scale) = to_scaled(left);
    let (b_negative, b_mantissa, b_scale) = to_scaled(right);

    // Sign only decides the answer when at least one side is non-zero: negative zero and positive
    // zero are the same number.
    if a_negative != b_negative {
        if a_mantissa == 0 && b_mantissa == 0 {
            return CompareOutcome::Ordered(core::cmp::Ordering::Equal);
        }

        return CompareOutcome::Ordered(if a_negative {
            core::cmp::Ordering::Less
        } else {
            core::cmp::Ordering::Greater
        });
    }

    let magnitude = compare_magnitude(a_mantissa, a_scale, b_mantissa, b_scale);
    CompareOutcome::Ordered(if a_negative {
        magnitude.reverse()
    } else {
        magnitude
    })
}

/// Compares `a / 10^a_scale` against `b / 10^b_scale`, both non-negative.
fn compare_magnitude(a: u128, a_scale: u32, b: u128, b_scale: u32) -> core::cmp::Ordering {
    if a_scale == b_scale {
        return a.cmp(&b);
    }

    // Bring the smaller scale up to the larger one. The multiplication can overflow u128, but only
    // when the scaled side is already far larger than any 96-bit mantissa can reach, so the
    // overflow is itself the answer.
    if a_scale < b_scale {
        match scale_up(a, b_scale - a_scale) {
            Some(scaled) => scaled.cmp(&b),
            None => core::cmp::Ordering::Greater,
        }
    } else {
        match scale_up(b, a_scale - b_scale) {
            Some(scaled) => a.cmp(&scaled),
            None => core::cmp::Ordering::Less,
        }
    }
}

#[inline]
fn scale_up(value: u128, exponent: u32) -> Option<u128> {
    let mut result = value;
    for _ in 0..exponent {
        result = result.checked_mul(10)?;
    }

    Some(result)
}

/// Decomposes an integer or decimal into (negative, mantissa, scale).
fn to_scaled(value: &Value) -> (bool, u128, u32) {
    match value {
        Value::I32(v) => (*v < 0, (*v as i64).unsigned_abs() as u128, 0),
        Value::I64(v) => (*v < 0, v.unsigned_abs() as u128, 0),
        Value::Decimal(lo, hi) => (
            value::decimal_is_negative(*hi),
            value::decimal_mantissa(*lo, *hi),
            value::decimal_scale(*hi),
        ),
        _ => (false, 0, 0),
    }
}

#[inline]
fn as_i64(value: &Value) -> i64 {
    match value {
        Value::I32(v) => *v as i64,
        Value::I64(v) => *v,
        _ => 0,
    }
}

#[inline]
fn as_f64(value: &Value) -> f64 {
    match value {
        Value::I32(v) => *v as f64,
        Value::I64(v) => *v as f64,
        Value::F64(v) => *v,
        _ => 0.0,
    }
}

const PERCENT: u16 = b'%' as u16;
const UNDERSCORE: u16 = b'_' as u16;
const BACKSLASH: u16 = b'\\' as u16;

/// SQL `LIKE`: `%` matches any run, `_` matches exactly one, `\` escapes either.
pub fn like(text: &[u8], pattern: &[u8]) -> bool {
    // The wildcards are ASCII, so for ASCII input the matcher runs straight over the UTF-8 bytes:
    // one byte is one UTF-16 code unit, which is what `_` counts. This is the path a real scan
    // takes, and it allocates nothing.
    if text.is_ascii() && pattern.is_ascii() {
        return like_generic(
            text.len(),
            pattern.len(),
            |i| text[i] as u16,
            |i| pattern[i] as u16,
        );
    }

    let text: Vec<u16> = Utf16Units::new(text).collect();
    let pattern: Vec<u16> = Utf16Units::new(pattern).collect();
    like_generic(text.len(), pattern.len(), |i| text[i], |i| pattern[i])
}

/// A port of `CuteFunctions.LikeMatch`, single backtrack point included.
///
/// That one backtrack is what keeps a pattern like `%a%a%a%a%b` linear rather than exponential: a
/// mismatch after a `%` resumes one character past where that `%` started matching, instead of
/// re-exploring every earlier split.
fn like_generic<T, P>(text_len: usize, pattern_len: usize, text: T, pattern: P) -> bool
where
    T: Fn(usize) -> u16,
    P: Fn(usize) -> u16,
{
    let mut text_index = 0usize;
    let mut pattern_index = 0usize;
    let mut star_text: isize = -1;
    let mut star_pattern: isize = -1;

    while text_index < text_len {
        let pattern_char = if pattern_index < pattern_len {
            Some(pattern(pattern_index))
        } else {
            None
        };

        let matched = match pattern_char {
            Some(BACKSLASH) if pattern_index + 1 < pattern_len => {
                if text(text_index) == pattern(pattern_index + 1) {
                    text_index += 1;
                    pattern_index += 2;
                    true
                } else {
                    false
                }
            }
            Some(UNDERSCORE) => {
                text_index += 1;
                pattern_index += 1;
                true
            }
            Some(c) if c == text(text_index) => {
                text_index += 1;
                pattern_index += 1;
                true
            }
            Some(PERCENT) => {
                star_pattern = pattern_index as isize;
                pattern_index += 1;
                star_text = text_index as isize;
                true
            }
            _ => false,
        };

        if matched {
            continue;
        }

        if star_pattern < 0 {
            return false;
        }

        pattern_index = (star_pattern + 1) as usize;
        star_text += 1;
        text_index = star_text as usize;
    }

    while pattern_index < pattern_len && pattern(pattern_index) == PERCENT {
        pattern_index += 1;
    }

    pattern_index == pattern_len
}

/// Yields the UTF-16 code units of a UTF-8 buffer, so ordinal comparisons match .NET's.
struct Utf16Units<'a> {
    bytes: &'a [u8],
    position: usize,
    pending_low_surrogate: Option<u16>,
}

impl<'a> Utf16Units<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Utf16Units {
            bytes,
            position: 0,
            pending_low_surrogate: None,
        }
    }

    #[inline]
    fn continuation(&self, offset: usize) -> u8 {
        self.bytes
            .get(self.position + offset)
            .map(|b| b & 0x3F)
            .unwrap_or(0)
    }
}

impl Iterator for Utf16Units<'_> {
    type Item = u16;

    fn next(&mut self) -> Option<u16> {
        if let Some(low) = self.pending_low_surrogate.take() {
            return Some(low);
        }

        if self.position >= self.bytes.len() {
            return None;
        }

        let first = self.bytes[self.position];

        // Anything that is not valid UTF-8 is treated byte-wise rather than rejected; a scan must
        // not fail on odd data.
        let (code_point, width) = if first < 0x80 {
            (first as u32, 1)
        } else if first & 0xE0 == 0xC0 {
            (
                ((first as u32 & 0x1F) << 6) | self.continuation(1) as u32,
                2,
            )
        } else if first & 0xF0 == 0xE0 {
            (
                ((first as u32 & 0x0F) << 12)
                    | ((self.continuation(1) as u32) << 6)
                    | self.continuation(2) as u32,
                3,
            )
        } else if first & 0xF8 == 0xF0 {
            (
                ((first as u32 & 0x07) << 18)
                    | ((self.continuation(1) as u32) << 12)
                    | ((self.continuation(2) as u32) << 6)
                    | self.continuation(3) as u32,
                4,
            )
        } else {
            (first as u32, 1)
        };

        self.position += width;

        if code_point > 0xFFFF {
            let adjusted = code_point - 0x1_0000;
            self.pending_low_surrogate = Some(0xDC00 + (adjusted & 0x3FF) as u16);
            Some(0xD800 + (adjusted >> 10) as u16)
        } else {
            Some(code_point as u16)
        }
    }
}
