'use strict';

/**
 * RESP2 encoding and decoding.
 *
 * MemSharp speaks the same wire protocol Redis does, so this module is deliberately generic:
 * encode a command as an array of bulk strings, decode a reply. Nothing here knows what a MemSharp
 * command means.
 */

const CRLF = Buffer.from('\r\n');

/** An error reply from the server. `code` is the leading token and is part of the wire contract. */
class MemSharpError extends Error {
  constructor(code, message) {
    super(`${code} ${message}`);
    this.name = 'MemSharpError';
    this.code = code;
    this.detail = message;
  }
}

/** A command was applied to a key holding a different type. */
class WrongTypeError extends MemSharpError {
  constructor(message) {
    super('WRONGTYPE', message);
    this.name = 'WrongTypeError';
  }
}

/** The connection closed before a complete reply arrived. */
class ConnectionClosedError extends MemSharpError {
  constructor(message = 'connection closed by the server') {
    super('CONN', message);
    this.name = 'ConnectionClosedError';
  }
}

/**
 * Encode a command as a RESP array of bulk strings.
 *
 * Every argument becomes a bulk string, because RESP has no types on the request side. Numbers are
 * stringified with the JavaScript default, which round-trips a double exactly - `toFixed` would not,
 * and would silently change a score on its way into a sorted set.
 */
function encodeCommand(args) {
  const parts = [Buffer.from(`*${args.length}\r\n`)];

  for (const arg of args) {
    const payload = Buffer.isBuffer(arg)
      ? arg
      : Buffer.from(arg === null || arg === undefined ? '' : String(arg), 'utf8');

    parts.push(Buffer.from(`$${payload.length}\r\n`), payload, CRLF);
  }

  return Buffer.concat(parts);
}

/**
 * An incremental RESP reply decoder.
 *
 * Feed it bytes as they arrive and ask for replies. It buffers whatever it cannot yet parse, so a
 * reply split across TCP segments is handled without the caller knowing, and a pipelined batch
 * yields several replies from one read.
 */
class Decoder {
  constructor() {
    this._buffer = Buffer.alloc(0);
  }

  /** Add bytes received from the socket. */
  feed(chunk) {
    this._buffer = this._buffer.length === 0 ? chunk : Buffer.concat([this._buffer, chunk]);
  }

  /**
   * Take one reply.
   * @returns {{ready: boolean, value: *}} `ready` is false when more bytes are needed.
   */
  take() {
    const result = parse(this._buffer, 0);
    if (!result.ready) return { ready: false, value: null };

    this._buffer = this._buffer.subarray(result.offset);
    return { ready: true, value: result.value };
  }
}

/** Parse one value at `start`. Returns `{ready, value, offset}`. */
function parse(buffer, start) {
  if (start >= buffer.length) return { ready: false };

  const lineEnd = buffer.indexOf('\r\n', start, 'utf8');
  if (lineEnd < 0) return { ready: false };

  const marker = buffer[start];
  const line = buffer.toString('utf8', start + 1, lineEnd);
  const afterLine = lineEnd + 2;

  switch (marker) {
    case 0x2b: // '+'
      return { ready: true, value: line, offset: afterLine };

    case 0x2d: { // '-'
      const space = line.indexOf(' ');
      const code = space > 0 ? line.slice(0, space) : 'ERR';
      const message = space > 0 ? line.slice(space + 1) : line;
      const error = code === 'WRONGTYPE' ? new WrongTypeError(message) : new MemSharpError(code, message);
      return { ready: true, value: error, offset: afterLine };
    }

    case 0x3a: // ':'
      return { ready: true, value: Number(line), offset: afterLine };

    case 0x24: { // '$'
      const length = Number(line);
      if (length < 0) return { ready: true, value: null, offset: afterLine };

      const end = afterLine + length;
      if (buffer.length < end + 2) return { ready: false };
      return { ready: true, value: buffer.toString('utf8', afterLine, end), offset: end + 2 };
    }

    case 0x2a: { // '*'
      const count = Number(line);
      if (count < 0) return { ready: true, value: null, offset: afterLine };

      const items = [];
      let offset = afterLine;
      for (let i = 0; i < count; i++) {
        const element = parse(buffer, offset);

        // A partial element means the whole array is partial: report not-ready so the next attempt
        // re-parses it from the top rather than leaving half an array consumed.
        if (!element.ready) return { ready: false };
        items.push(element.value);
        offset = element.offset;
      }
      return { ready: true, value: items, offset };
    }

    default:
      throw new MemSharpError('ERR', `unknown RESP type marker '${String.fromCharCode(marker)}'`);
  }
}

/** Flatten `{a: 1, b: 2}` into `['a', 1, 'b', 2]` for commands that take pairs. */
function flatten(object) {
  const result = [];
  for (const [key, value] of Object.entries(object)) {
    result.push(key, value);
  }
  return result;
}

/** Pair a flat `[k, v, k, v]` reply back into an object. */
function unflatten(flat) {
  const result = {};
  for (let i = 0; i + 1 < flat.length; i += 2) result[flat[i]] = flat[i + 1];
  return result;
}

module.exports = {
  MemSharpError,
  WrongTypeError,
  ConnectionClosedError,
  encodeCommand,
  Decoder,
  flatten,
  unflatten,
};
