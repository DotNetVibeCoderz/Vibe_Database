'use strict';

/**
 * MemSharp client for Node.js.
 *
 * MemSharp speaks RESP2, so this is a thin, dependency-free client over a socket.
 *
 * @example
 * const { MemSharpClient } = require('memsharp');
 *
 * const db = new MemSharpClient({ port: 6380 });
 * await db.connect();
 * await db.set('symbol:BTC', '68350.25');
 * await db.zadd('book:BTC:bids', { 'bid-1': 68350.25 });
 * await db.close();
 */

module.exports = require('./src/client');
