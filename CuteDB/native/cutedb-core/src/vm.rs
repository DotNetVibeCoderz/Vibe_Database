//! The predicate virtual machine.
//!
//! A tiny stack machine over [`Value`], executing the bytecode `PredicateProgram` emits. The whole
//! point is that a scan runs the program once per document without ever crossing back into managed
//! code, so the per-row cost is a handful of comparisons and a path walk rather than a P/Invoke.
//!
//! Three-valued logic is preserved exactly: a comparison against an absent field pushes `Missing`,
//! not `false`, and only the final truthiness test collapses it. Anything the VM cannot reproduce
//! faithfully — an unknown opcode, damaged data, a decimal weighed against a double — stops the
//! whole scan with a status code, and the managed evaluator answers the query instead.

use crate::compare::{self, CompareOp, Truth};
use crate::path::Path;
use crate::value::{self, Value};

/// Opcodes. Shared with `PredicateOp` on the managed side.
pub mod op {
    pub const PUSH_PATH: u8 = 0x01;
    pub const PUSH_CONST: u8 = 0x02;

    pub const EQUAL: u8 = 0x10;
    pub const NOT_EQUAL: u8 = 0x11;
    pub const LESS: u8 = 0x12;
    pub const LESS_OR_EQUAL: u8 = 0x13;
    pub const GREATER: u8 = 0x14;
    pub const GREATER_OR_EQUAL: u8 = 0x15;
    pub const IN: u8 = 0x16;
    pub const LIKE: u8 = 0x17;
    pub const NOT_LIKE: u8 = 0x18;
    pub const BETWEEN: u8 = 0x19;
    pub const NOT_BETWEEN: u8 = 0x1A;
    pub const IS_NULL: u8 = 0x1B;
    pub const IS_NOT_NULL: u8 = 0x1C;
    pub const IS_MISSING: u8 = 0x1D;
    pub const IS_NOT_MISSING: u8 = 0x1E;
    pub const NOT_IN: u8 = 0x1F;

    pub const AND: u8 = 0x20;
    pub const OR: u8 = 0x21;
    pub const NOT: u8 = 0x22;

    pub const RETURN: u8 = 0xFF;
}

/// The bytecode ABI this build implements. Must equal `PredicateProgram.AbiVersion`.
pub const ABI_VERSION: u32 = 1;

const MAGIC: u32 = 0x5054_5543; // 'CUTP'
const MAX_STACK: usize = 64;

/// Why a scan stopped early.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VmError {
    /// The program blob is not a predicate program, or is truncated.
    BadProgram,
    /// The program was built for a different bytecode version.
    AbiMismatch,
    /// An opcode this build does not implement.
    UnknownOpcode,
    /// The stack under- or overflowed, which means the program is malformed.
    StackFault,
    /// A document did not decode.
    BadDocument,
    /// A comparison this build cannot reproduce exactly; the caller should fall back.
    Unsupported,
}

impl VmError {
    /// The negative status code the FFI layer reports.
    pub fn code(self) -> i32 {
        match self {
            VmError::BadProgram => -1,
            VmError::AbiMismatch => -2,
            VmError::UnknownOpcode => -3,
            VmError::StackFault => -4,
            VmError::BadDocument => -5,
            VmError::Unsupported => -6,
        }
    }
}

/// A parsed predicate program, ready to run against any number of documents.
pub struct Program<'p> {
    paths: Vec<Path<'p>>,
    constants: Vec<&'p [u8]>,
    code: &'p [u8],
}

impl<'p> Program<'p> {
    /// Parses the program blob.
    pub fn parse(data: &'p [u8]) -> Result<Program<'p>, VmError> {
        if data.len() < 16 {
            return Err(VmError::BadProgram);
        }

        let magic = u32::from_le_bytes([data[0], data[1], data[2], data[3]]);
        if magic != MAGIC {
            return Err(VmError::BadProgram);
        }

        let abi = u32::from_le_bytes([data[4], data[5], data[6], data[7]]);
        if abi != ABI_VERSION {
            return Err(VmError::AbiMismatch);
        }

        let path_count = u32::from_le_bytes([data[8], data[9], data[10], data[11]]) as usize;
        let const_count = u32::from_le_bytes([data[12], data[13], data[14], data[15]]) as usize;

        let mut cursor = 16;
        let mut paths = Vec::with_capacity(path_count);
        for _ in 0..path_count {
            let rest = data.get(cursor..).ok_or(VmError::BadProgram)?;
            let (path, used) = Path::decode(rest).map_err(|_| VmError::BadProgram)?;
            paths.push(path);
            cursor += used;
        }

        let mut constants = Vec::with_capacity(const_count);
        for _ in 0..const_count {
            let header = data.get(cursor..cursor + 4).ok_or(VmError::BadProgram)?;
            let length = u32::from_le_bytes([header[0], header[1], header[2], header[3]]) as usize;
            cursor += 4;

            let body = data
                .get(cursor..cursor + length)
                .ok_or(VmError::BadProgram)?;
            constants.push(body);
            cursor += length;
        }

        let header = data.get(cursor..cursor + 4).ok_or(VmError::BadProgram)?;
        let code_len = u32::from_le_bytes([header[0], header[1], header[2], header[3]]) as usize;
        cursor += 4;

        let code = data
            .get(cursor..cursor + code_len)
            .ok_or(VmError::BadProgram)?;

        Ok(Program {
            paths,
            constants,
            code,
        })
    }

    /// Runs the program against one encoded document.
    ///
    /// The operand stack is a fixed-size array living in this frame, not a `Vec`. A heap
    /// allocation here would happen once per document, and on a million-row scan that single
    /// allocation is enough to make the accelerator slower than the managed evaluator it replaces.
    pub fn test(&self, document: &[u8]) -> Result<bool, VmError> {
        let mut stack = Stack::new();
        let mut pc = 0usize;

        while pc < self.code.len() {
            let opcode = self.code[pc];
            pc += 1;

            match opcode {
                op::PUSH_PATH => {
                    let index = self.read_operand(&mut pc)?;
                    let path = self.paths.get(index).ok_or(VmError::BadProgram)?;
                    let value = path.resolve(document).map_err(|_| VmError::BadDocument)?;
                    stack.push(value)?;
                }

                op::PUSH_CONST => {
                    let index = self.read_operand(&mut pc)?;
                    let raw = self.constants.get(index).ok_or(VmError::BadProgram)?;
                    let value = value::decode(raw).map_err(|_| VmError::BadProgram)?;
                    stack.push(value)?;
                }

                op::EQUAL
                | op::NOT_EQUAL
                | op::LESS
                | op::LESS_OR_EQUAL
                | op::GREATER
                | op::GREATER_OR_EQUAL => {
                    let right = stack.pop()?;
                    let left = stack.pop()?;
                    let comparison = match opcode {
                        op::EQUAL => CompareOp::Equal,
                        op::NOT_EQUAL => CompareOp::NotEqual,
                        op::LESS => CompareOp::Less,
                        op::LESS_OR_EQUAL => CompareOp::LessOrEqual,
                        op::GREATER => CompareOp::Greater,
                        _ => CompareOp::GreaterOrEqual,
                    };

                    stack.push(truth_to_value(compare::compare_op(
                        comparison, &left, &right,
                    ))?)?;
                }

                op::IN | op::NOT_IN => {
                    let candidates = stack.pop()?;
                    let value = stack.pop()?;
                    let negated = opcode == op::NOT_IN;
                    stack.push(self.evaluate_in(&value, &candidates, negated)?)?;
                }

                op::LIKE | op::NOT_LIKE => {
                    let pattern = stack.pop()?;
                    let value = stack.pop()?;

                    if value.is_null_or_missing() {
                        stack.push(Value::Missing)?;
                    } else {
                        let matched = match (value, pattern) {
                            (Value::Str(text), Value::Str(glob)) => compare::like(text, glob),
                            _ => false,
                        };

                        stack.push(Value::Bool(matched != (opcode == op::NOT_LIKE)))?;
                    }
                }

                op::BETWEEN | op::NOT_BETWEEN => {
                    let high = stack.pop()?;
                    let low = stack.pop()?;
                    let value = stack.pop()?;

                    if value.is_null_or_missing()
                        || low.is_null_or_missing()
                        || high.is_null_or_missing()
                    {
                        stack.push(Value::Missing)?;
                        continue;
                    }

                    let above = compare::compare_op(CompareOp::GreaterOrEqual, &value, &low);
                    let below = compare::compare_op(CompareOp::LessOrEqual, &value, &high);
                    if above == Truth::Unsupported || below == Truth::Unsupported {
                        return Err(VmError::Unsupported);
                    }

                    let in_range = above.is_true() && below.is_true();
                    stack.push(Value::Bool(in_range != (opcode == op::NOT_BETWEEN)))?;
                }

                op::IS_NULL | op::IS_NOT_NULL | op::IS_MISSING | op::IS_NOT_MISSING => {
                    let value = stack.pop()?;
                    let matches = match opcode {
                        op::IS_NULL | op::IS_NOT_NULL => value.is_null_or_missing(),
                        _ => value.is_missing(),
                    };

                    let negated = matches!(opcode, op::IS_NOT_NULL | op::IS_NOT_MISSING);
                    stack.push(Value::Bool(matches != negated))?;
                }

                op::AND => {
                    let right = stack.pop()?;
                    let left = stack.pop()?;
                    stack.push(Value::Bool(left.is_truthy() && right.is_truthy()))?;
                }

                op::OR => {
                    let right = stack.pop()?;
                    let left = stack.pop()?;
                    stack.push(Value::Bool(left.is_truthy() || right.is_truthy()))?;
                }

                op::NOT => {
                    let value = stack.pop()?;
                    if value.is_null_or_missing() {
                        stack.push(Value::Missing)?;
                    } else {
                        stack.push(Value::Bool(!value.is_truthy()))?;
                    }
                }

                op::RETURN => return Ok(stack.pop()?.is_truthy()),

                _ => return Err(VmError::UnknownOpcode),
            }
        }

        // Falling off the end without a RETURN means the program was truncated.
        Err(VmError::BadProgram)
    }

    fn evaluate_in<'a>(
        &self,
        value: &Value<'a>,
        candidates: &Value<'a>,
        negated: bool,
    ) -> Result<Value<'a>, VmError> {
        if value.is_missing() {
            return Ok(Value::Missing);
        }

        let raw = match candidates {
            Value::Array(raw) => *raw,
            _ => return Err(VmError::BadProgram),
        };

        let iterator = value::ArrayIter::new(raw).map_err(|_| VmError::BadProgram)?;
        let mut found = false;

        for item in iterator {
            let item = item
                .and_then(value::decode)
                .map_err(|_| VmError::BadProgram)?;

            match compare::compare_op(CompareOp::Equal, value, &item) {
                Truth::True => {
                    found = true;
                    break;
                }
                Truth::Unsupported => return Err(VmError::Unsupported),
                _ => {}
            }
        }

        Ok(Value::Bool(found != negated))
    }

    #[inline]
    fn read_operand(&self, pc: &mut usize) -> Result<usize, VmError> {
        let bytes = self.code.get(*pc..*pc + 2).ok_or(VmError::BadProgram)?;
        *pc += 2;
        Ok(u16::from_le_bytes([bytes[0], bytes[1]]) as usize)
    }
}

#[inline]
fn truth_to_value<'a>(truth: Truth) -> Result<Value<'a>, VmError> {
    match truth {
        Truth::True => Ok(Value::Bool(true)),
        Truth::False => Ok(Value::Bool(false)),
        Truth::Unknown => Ok(Value::Missing),
        Truth::Unsupported => Err(VmError::Unsupported),
    }
}

/// The operand stack: a fixed array plus a depth, so running a predicate allocates nothing.
///
/// `MAX_STACK` is generous next to what real predicates need — the compiler emits at most three
/// operands before any instruction consumes them, and nesting only deepens it by one per level —
/// so overflowing it means a malformed program rather than an unusually complex query.
struct Stack<'a> {
    slots: [Value<'a>; MAX_STACK],
    depth: usize,
}

impl<'a> Stack<'a> {
    #[inline]
    fn new() -> Self {
        Stack {
            slots: [Value::Missing; MAX_STACK],
            depth: 0,
        }
    }

    #[inline]
    fn push(&mut self, value: Value<'a>) -> Result<(), VmError> {
        if self.depth >= MAX_STACK {
            return Err(VmError::StackFault);
        }

        self.slots[self.depth] = value;
        self.depth += 1;
        Ok(())
    }

    #[inline]
    fn pop(&mut self) -> Result<Value<'a>, VmError> {
        if self.depth == 0 {
            return Err(VmError::StackFault);
        }

        self.depth -= 1;
        Ok(self.slots[self.depth])
    }
}
