//! Unit tests for the pieces that are easiest to get subtly wrong.
//!
//! The real safety net is the managed parity suite, which runs the same predicates through both
//! implementations over generated data. These cover the arithmetic and text edge cases where
//! a mismatch would be hard to trace back from a failing scan.

use crate::compare::{compare, compare_op, like, CompareOp, CompareOutcome, Truth};
use crate::value::{self, Value};
use core::cmp::Ordering;

/// Builds a .NET decimal's raw halves from a mantissa, scale and sign.
fn decimal(mantissa: u128, scale: u32, negative: bool) -> Value<'static> {
    let lo = (mantissa & 0xFFFF_FFFF_FFFF_FFFF) as u64;
    let mantissa_high = ((mantissa >> 64) & 0xFFFF_FFFF) as u64;
    let flags = ((scale as u64) << 16) | if negative { 1u64 << 31 } else { 0 };
    Value::Decimal(lo, (flags << 32) | mantissa_high)
}

fn ordering(left: &Value, right: &Value) -> Ordering {
    match compare(left, right) {
        CompareOutcome::Ordered(order) => order,
        other => panic!("expected an ordering, got {other:?}"),
    }
}

#[test]
fn decimal_layout_round_trips() {
    // 123.45 is mantissa 12345 at scale 2.
    let value = decimal(12_345, 2, false);
    let Value::Decimal(lo, hi) = value else {
        panic!("not a decimal")
    };

    assert_eq!(value::decimal_mantissa(lo, hi), 12_345);
    assert_eq!(value::decimal_scale(hi), 2);
    assert!(!value::decimal_is_negative(hi));
}

#[test]
fn decimals_compare_exactly_across_scales() {
    // 1.10 and 1.1 are the same number stored at different scales.
    assert_eq!(
        ordering(&decimal(110, 2, false), &decimal(11, 1, false)),
        Ordering::Equal
    );
    assert_eq!(
        ordering(&decimal(12_345, 2, false), &decimal(1_234, 1, false)),
        Ordering::Greater
    );
    assert_eq!(
        ordering(&decimal(1, 0, true), &decimal(1, 0, false)),
        Ordering::Less
    );

    // Negative zero and positive zero are the same value.
    assert_eq!(
        ordering(&decimal(0, 0, true), &decimal(0, 0, false)),
        Ordering::Equal
    );
}

#[test]
fn decimal_scaling_overflow_still_orders_correctly() {
    // A 96-bit mantissa scaled up by 28 overflows u128. The overflow itself proves which side is
    // larger, and the comparison must not wrap around into the wrong answer.
    let huge = decimal(u128::MAX >> 32, 0, false);
    let tiny = decimal(1, 28, false);

    assert_eq!(ordering(&huge, &tiny), Ordering::Greater);
    assert_eq!(ordering(&tiny, &huge), Ordering::Less);
}

#[test]
fn integers_and_decimals_compare_without_floating_point() {
    assert_eq!(
        ordering(&Value::I32(2), &decimal(200, 2, false)),
        Ordering::Equal
    );
    assert_eq!(
        ordering(&Value::I64(3), &decimal(29_999, 4, false)),
        Ordering::Greater
    );
}

#[test]
fn decimal_against_double_is_declined_rather_than_guessed() {
    assert_eq!(
        compare(&decimal(1, 1, false), &Value::F64(0.1)),
        CompareOutcome::Unsupported
    );
}

#[test]
fn nan_sorts_below_everything_and_equals_itself() {
    assert_eq!(
        ordering(&Value::F64(f64::NAN), &Value::F64(1.0)),
        Ordering::Less
    );
    assert_eq!(
        ordering(&Value::F64(f64::NAN), &Value::F64(f64::NAN)),
        Ordering::Equal
    );
}

#[test]
fn type_ranks_order_unlike_values() {
    assert_eq!(ordering(&Value::Null, &Value::Bool(false)), Ordering::Less);
    assert_eq!(ordering(&Value::Bool(true), &Value::I32(0)), Ordering::Less);
    assert_eq!(ordering(&Value::I32(9), &Value::Str(b"a")), Ordering::Less);
    assert_eq!(ordering(&Value::Missing, &Value::Null), Ordering::Less);
}

#[test]
fn strings_compare_by_utf16_code_unit() {
    // U+FF21 (fullwidth A) is above the surrogate range in UTF-16, while U+10000 encodes as a
    // surrogate pair starting at D800 — so UTF-16 order puts the astral character first even
    // though its UTF-8 bytes are larger.
    let fullwidth = "\u{FF21}".as_bytes();
    let astral = "\u{10000}".as_bytes();

    assert_eq!(
        ordering(&Value::Str(astral), &Value::Str(fullwidth)),
        Ordering::Less
    );
    assert!(
        astral > fullwidth,
        "the UTF-8 byte order is the opposite way round"
    );
}

#[test]
fn comparisons_against_missing_are_unknown() {
    assert_eq!(
        compare_op(CompareOp::Greater, &Value::Missing, &Value::I32(1)),
        Truth::Unknown
    );
    assert_eq!(
        compare_op(CompareOp::Equal, &Value::Null, &Value::Null),
        Truth::True
    );
    assert_eq!(
        compare_op(CompareOp::Less, &Value::Null, &Value::I32(1)),
        Truth::Unknown
    );
}

#[test]
fn like_handles_wildcards_escapes_and_backtracking() {
    assert!(like(b"SO-001", b"SO-%"));
    assert!(like(b"SO-001", b"SO-00_"));
    assert!(!like(b"SO-001", b"SO-0_"));
    assert!(like(b"anything", b"%"));
    assert!(like(b"", b"%"));
    assert!(!like(b"", b"_"));
    assert!(like(b"50% off", b"50\\% off"));
    assert!(!like(b"50x off", b"50\\% off"));

    // The pattern that makes a naive recursive matcher blow up.
    assert!(!like(
        b"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaac",
        b"%a%a%a%a%a%a%a%b"
    ));
}

#[test]
fn hash_is_stable() {
    assert_eq!(crate::hash64(b""), 0xcbf2_9ce4_8422_2325);
    assert_ne!(crate::hash64(b"cutedb"), crate::hash64(b"cutedc"));
}
