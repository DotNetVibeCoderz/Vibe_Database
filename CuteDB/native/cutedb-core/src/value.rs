//! Reading CuteDB's binary document format.
//!
//! This is the Rust half of the encoding defined in `src/CuteDB/Serialization/CuteBinary.cs`. The
//! tag numbers and the layout of every payload are shared constants; if one side changes, the
//! other has to change in the same commit, and `PredicateProgram.AbiVersion` is what stops a
//! mismatched pair from ever running together.
//!
//! Nothing here allocates. A [`Value`] borrows straight out of the slab it was read from, so
//! walking a million documents to test one field touches no heap at all.

/// A value tag. Mirrors `CuteType` on the managed side.
pub mod tag {
    pub const NULL: u8 = 0x00;
    pub const FALSE: u8 = 0x01;
    pub const TRUE: u8 = 0x02;
    pub const I32: u8 = 0x03;
    pub const I64: u8 = 0x04;
    pub const F64: u8 = 0x05;
    pub const STRING: u8 = 0x06;
    pub const BINARY: u8 = 0x07;
    pub const ARRAY: u8 = 0x08;
    pub const OBJECT: u8 = 0x09;
    pub const DATETIME: u8 = 0x0A;
    pub const GUID: u8 = 0x0B;
    pub const DECIMAL: u8 = 0x0C;
    pub const ID: u8 = 0x0D;
}

/// A decoded value, borrowing its payload from the buffer it was read out of.
#[derive(Clone, Copy, Debug)]
pub enum Value<'a> {
    /// The path did not resolve. Never appears in stored data.
    Missing,
    Null,
    Bool(bool),
    I32(i32),
    I64(i64),
    F64(f64),
    /// A .NET `decimal`, as the raw low and high 64-bit halves the managed side writes.
    Decimal(u64, u64),
    /// UTF-8 bytes, without the length prefix.
    Str(&'a [u8]),
    Binary(&'a [u8]),
    /// The whole encoded array, tag byte included.
    Array(&'a [u8]),
    /// The whole encoded object, tag byte included.
    Object(&'a [u8]),
    /// UTC tick count.
    DateTime(i64),
    Guid(u64, u64),
    Id([u8; 12]),
}

impl<'a> Value<'a> {
    /// The sort rank of this value's type. Must match `CuteValueComparer.TypeRank`.
    pub fn type_rank(&self) -> i32 {
        match self {
            Value::Missing => 0,
            Value::Null => 1,
            Value::Bool(_) => 2,
            Value::I32(_) | Value::I64(_) | Value::F64(_) | Value::Decimal(_, _) => 3,
            Value::Str(_) => 4,
            Value::Binary(_) => 5,
            Value::DateTime(_) => 6,
            Value::Guid(_, _) => 7,
            Value::Id(_) => 8,
            Value::Array(_) => 9,
            Value::Object(_) => 10,
        }
    }

    pub fn is_missing(&self) -> bool {
        matches!(self, Value::Missing)
    }

    pub fn is_null(&self) -> bool {
        matches!(self, Value::Null)
    }

    pub fn is_null_or_missing(&self) -> bool {
        matches!(self, Value::Null | Value::Missing)
    }

    pub fn is_array(&self) -> bool {
        matches!(self, Value::Array(_))
    }

    /// Truthiness, matching `CuteValue.IsTruthy`.
    pub fn is_truthy(&self) -> bool {
        match self {
            Value::Missing | Value::Null => false,
            Value::Bool(b) => *b,
            Value::I32(v) => *v != 0,
            Value::I64(v) => *v != 0,
            Value::F64(v) => *v != 0.0,
            Value::Decimal(lo, hi) => decimal_mantissa(*lo, *hi) != 0,
            Value::Str(bytes) => !bytes.is_empty(),
            Value::Binary(bytes) => !bytes.is_empty(),
            Value::Array(raw) => array_len(raw).unwrap_or(0) > 0,
            Value::Object(raw) => object_len(raw).unwrap_or(0) > 0,
            _ => true,
        }
    }
}

/// Something in the buffer did not decode. Every read returns this rather than panicking, so
/// damaged input becomes a status code instead of a crashed host process.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DecodeError;

pub type Result<T> = core::result::Result<T, DecodeError>;

/// Reads an unsigned LEB128 varint, returning the value and its width.
#[inline]
pub fn read_varint(data: &[u8]) -> Result<(u32, usize)> {
    let mut result: u32 = 0;
    let mut shift = 0;

    for i in 0..5 {
        let byte = *data.get(i).ok_or(DecodeError)?;
        result |= ((byte & 0x7F) as u32) << shift;
        if byte & 0x80 == 0 {
            return Ok((result, i + 1));
        }
        shift += 7;
    }

    Err(DecodeError)
}

#[inline]
fn read_u32(data: &[u8], offset: usize) -> Result<u32> {
    let slice = data.get(offset..offset + 4).ok_or(DecodeError)?;
    Ok(u32::from_le_bytes([slice[0], slice[1], slice[2], slice[3]]))
}

#[inline]
fn read_u64(data: &[u8], offset: usize) -> Result<u64> {
    let slice = data.get(offset..offset + 8).ok_or(DecodeError)?;
    Ok(u64::from_le_bytes([
        slice[0], slice[1], slice[2], slice[3], slice[4], slice[5], slice[6], slice[7],
    ]))
}

/// The total encoded length of the value at the start of `data`, without decoding it.
///
/// This is the operation the whole format is shaped around: a container carries its payload length
/// before its contents, so skipping a subtree of any size costs one 32-bit read and an addition.
pub fn skip(data: &[u8]) -> Result<usize> {
    let tag = *data.first().ok_or(DecodeError)?;
    match tag {
        tag::NULL | tag::FALSE | tag::TRUE => Ok(1),
        tag::I32 => Ok(5),
        tag::I64 | tag::F64 | tag::DATETIME => Ok(9),
        tag::GUID | tag::DECIMAL => Ok(17),
        tag::ID => Ok(13),
        tag::STRING | tag::BINARY => {
            let (length, width) = read_varint(&data[1..])?;
            Ok(1 + width + length as usize)
        }
        tag::ARRAY | tag::OBJECT => Ok(5 + read_u32(data, 1)? as usize),
        _ => Err(DecodeError),
    }
}

/// Decodes the value at the start of `data`.
pub fn decode(data: &[u8]) -> Result<Value<'_>> {
    let tag = *data.first().ok_or(DecodeError)?;
    let body = &data[1..];

    let value = match tag {
        tag::NULL => Value::Null,
        tag::FALSE => Value::Bool(false),
        tag::TRUE => Value::Bool(true),
        tag::I32 => Value::I32(read_u32(body, 0)? as i32),
        tag::I64 => Value::I64(read_u64(body, 0)? as i64),
        tag::F64 => Value::F64(f64::from_bits(read_u64(body, 0)?)),
        tag::DATETIME => Value::DateTime(read_u64(body, 0)? as i64),
        tag::GUID => Value::Guid(read_u64(body, 0)?, read_u64(body, 8)?),
        tag::DECIMAL => Value::Decimal(read_u64(body, 0)?, read_u64(body, 8)?),
        tag::ID => {
            let bytes = body.get(0..12).ok_or(DecodeError)?;
            let mut id = [0u8; 12];
            id.copy_from_slice(bytes);
            Value::Id(id)
        }
        tag::STRING => {
            let (length, width) = read_varint(body)?;
            Value::Str(
                body.get(width..width + length as usize)
                    .ok_or(DecodeError)?,
            )
        }
        tag::BINARY => {
            let (length, width) = read_varint(body)?;
            Value::Binary(
                body.get(width..width + length as usize)
                    .ok_or(DecodeError)?,
            )
        }
        tag::ARRAY => Value::Array(data.get(..skip(data)?).ok_or(DecodeError)?),
        tag::OBJECT => Value::Object(data.get(..skip(data)?).ok_or(DecodeError)?),
        _ => return Err(DecodeError),
    };

    Ok(value)
}

/// Finds a field inside an encoded object, skipping over the fields it does not want.
pub fn field<'a>(object: &'a [u8], key: &[u8]) -> Result<Option<&'a [u8]>> {
    if object.first().copied() != Some(tag::OBJECT) {
        return Ok(None);
    }

    let payload_len = read_u32(object, 1)? as usize;
    let payload = object.get(5..5 + payload_len).ok_or(DecodeError)?;

    let (count, mut cursor) = read_varint(payload)?;
    for _ in 0..count {
        let (key_len, key_width) = read_varint(payload.get(cursor..).ok_or(DecodeError)?)?;
        cursor += key_width;

        let candidate = payload
            .get(cursor..cursor + key_len as usize)
            .ok_or(DecodeError)?;
        cursor += key_len as usize;

        let value = payload.get(cursor..).ok_or(DecodeError)?;
        let value_len = skip(value)?;

        if candidate == key {
            return Ok(Some(&value[..value_len]));
        }

        cursor += value_len;
    }

    Ok(None)
}

/// Indexes into an encoded array. Negative indices count from the end.
pub fn element(array: &[u8], index: i32) -> Result<Option<&[u8]>> {
    if array.first().copied() != Some(tag::ARRAY) {
        return Ok(None);
    }

    let payload_len = read_u32(array, 1)? as usize;
    let payload = array.get(5..5 + payload_len).ok_or(DecodeError)?;

    let (count, mut cursor) = read_varint(payload)?;
    let count = count as i64;
    let effective = if index < 0 {
        count + index as i64
    } else {
        index as i64
    };

    if effective < 0 || effective >= count {
        return Ok(None);
    }

    for _ in 0..effective {
        cursor += skip(payload.get(cursor..).ok_or(DecodeError)?)?;
    }

    let target = payload.get(cursor..).ok_or(DecodeError)?;
    let length = skip(target)?;
    Ok(Some(&target[..length]))
}

/// The element count of an encoded array.
pub fn array_len(array: &[u8]) -> Result<u32> {
    if array.first().copied() != Some(tag::ARRAY) {
        return Err(DecodeError);
    }

    let payload_len = read_u32(array, 1)? as usize;
    let payload = array.get(5..5 + payload_len).ok_or(DecodeError)?;
    Ok(read_varint(payload)?.0)
}

/// The field count of an encoded object.
pub fn object_len(object: &[u8]) -> Result<u32> {
    if object.first().copied() != Some(tag::OBJECT) {
        return Err(DecodeError);
    }

    let payload_len = read_u32(object, 1)? as usize;
    let payload = object.get(5..5 + payload_len).ok_or(DecodeError)?;
    Ok(read_varint(payload)?.0)
}

/// Walks the elements of an encoded array as raw slices.
pub struct ArrayIter<'a> {
    payload: &'a [u8],
    cursor: usize,
    remaining: u32,
}

impl<'a> ArrayIter<'a> {
    pub fn new(array: &'a [u8]) -> Result<Self> {
        if array.first().copied() != Some(tag::ARRAY) {
            return Err(DecodeError);
        }

        let payload_len = read_u32(array, 1)? as usize;
        let payload = array.get(5..5 + payload_len).ok_or(DecodeError)?;
        let (remaining, cursor) = read_varint(payload)?;

        Ok(ArrayIter {
            payload,
            cursor,
            remaining,
        })
    }
}

impl<'a> Iterator for ArrayIter<'a> {
    type Item = Result<&'a [u8]>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.remaining == 0 {
            return None;
        }

        self.remaining -= 1;
        let rest = match self.payload.get(self.cursor..) {
            Some(rest) => rest,
            None => return Some(Err(DecodeError)),
        };

        match skip(rest) {
            Ok(length) => {
                let item = &rest[..length];
                self.cursor += length;
                Some(Ok(item))
            }
            Err(error) => Some(Err(error)),
        }
    }
}

/// The 96-bit mantissa of a .NET `decimal`, from the raw halves the managed side stores.
///
/// The managed layout packs `decimal.GetBits()` as `lo = bits[1] << 32 | bits[0]` and
/// `hi = bits[3] << 32 | bits[2]`, so bits\[3\] — which carries the sign and scale — ends up in
/// the top 32 bits of `hi`.
#[inline]
pub fn decimal_mantissa(lo: u64, hi: u64) -> u128 {
    ((hi as u128 & 0xFFFF_FFFF) << 64) | lo as u128
}

/// The scale (number of digits after the point), 0 to 28.
#[inline]
pub fn decimal_scale(hi: u64) -> u32 {
    ((hi >> 48) & 0xFF) as u32
}

/// True when the decimal is negative.
#[inline]
pub fn decimal_is_negative(hi: u64) -> bool {
    (hi >> 63) & 1 == 1
}
