//! Decoding and resolving the compiled field paths the managed side ships across.
//!
//! The wire form is produced by `CutePath.Encode`: a varint segment count followed by segments,
//! each a kind byte and its payload. Only field and index segments appear here — a path that
//! projects across an array (`lines[].sku`) is refused by the managed compiler before the program
//! is ever built, because reproducing a projection natively would mean materialising an array to
//! compare against, and a scan that allocates per row is not worth accelerating.

use crate::value::{self, DecodeError, Result, Value};

/// One step in a path.
#[derive(Debug, Clone, Copy)]
pub enum Segment<'a> {
    /// A field name, as raw UTF-8 so lookups compare bytes directly.
    Field(&'a [u8]),
    /// An array index; negative counts from the end.
    Index(i32),
}

/// A decoded path.
#[derive(Debug, Clone)]
pub struct Path<'a> {
    segments: Vec<Segment<'a>>,
}

impl<'a> Path<'a> {
    /// Reads one encoded path, returning it and how many bytes it occupied.
    pub fn decode(data: &'a [u8]) -> Result<(Path<'a>, usize)> {
        let (count, mut cursor) = value::read_varint(data)?;
        let mut segments = Vec::with_capacity(count as usize);

        for _ in 0..count {
            let kind = *data.get(cursor).ok_or(DecodeError)?;
            cursor += 1;

            match kind {
                0 => {
                    let (length, width) =
                        value::read_varint(data.get(cursor..).ok_or(DecodeError)?)?;
                    cursor += width;
                    let name = data
                        .get(cursor..cursor + length as usize)
                        .ok_or(DecodeError)?;
                    cursor += length as usize;
                    segments.push(Segment::Field(name));
                }
                1 => {
                    let (raw, width) = value::read_varint(data.get(cursor..).ok_or(DecodeError)?)?;
                    cursor += width;
                    segments.push(Segment::Index(raw as i32));
                }

                // Kind 2 is a projection. The managed compiler never emits one into a program, so
                // seeing it means the two sides are out of step.
                _ => return Err(DecodeError),
            }
        }

        Ok((Path { segments }, cursor))
    }

    /// Resolves the path against an encoded document, yielding `Missing` when it does not match.
    pub fn resolve<'d>(&self, document: &'d [u8]) -> Result<Value<'d>> {
        let mut current = document;

        for segment in &self.segments {
            let next = match segment {
                Segment::Field(name) => value::field(current, name)?,
                Segment::Index(index) => value::element(current, *index)?,
            };

            match next {
                Some(slice) => current = slice,
                None => return Ok(Value::Missing),
            }
        }

        value::decode(current)
    }
}
