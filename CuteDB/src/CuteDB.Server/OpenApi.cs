namespace CuteDB.Server;

/// <summary>
/// The OpenAPI description served at <c>/openapi.json</c>.
/// </summary>
/// <remarks>
/// Written out by hand rather than generated. The endpoints take and return schemaless documents,
/// which a generator can only describe as "an object", so the generated document would be longer,
/// need a package, and say less than this one does.
/// </remarks>
internal static class OpenApi
{
    /// <summary>The document, as JSON.</summary>
    internal const string Document = """
        {
          "openapi": "3.1.0",
          "info": {
            "title": "CuteDB HTTP API",
            "version": "2.0.0",
            "summary": "An HTTP API over one embedded CuteDB database.",
            "description": "Documents are schemaless JSON. Every endpoint that accepts or returns a document passes it through unchanged, keeping decimals exact and dates typed. Built by Gravicode Studios, led by Kang Fadhil.",
            "license": { "name": "MIT" },
            "contact": { "url": "https://github.com/DotNetVibeCoderz/Vibe_Database/tree/main/CuteDB" }
          },
          "servers": [{ "url": "http://127.0.0.1:8420", "description": "Default local server" }],
          "components": {
            "securitySchemes": {
              "apiKey": { "type": "apiKey", "in": "header", "name": "X-API-Key" },
              "bearer": { "type": "http", "scheme": "bearer" }
            },
            "schemas": {
              "Document": {
                "type": "object",
                "description": "Any JSON object. The reserved _id field holds the document's 24-character identifier.",
                "properties": { "_id": { "type": "string", "pattern": "^[0-9a-f]{24}$" } },
                "additionalProperties": true
              },
              "QueryResult": {
                "type": "object",
                "properties": {
                  "kind": { "type": "string", "enum": ["select", "insert", "update", "delete"] },
                  "columns": { "type": "array", "items": { "type": "string" },
                    "description": "Field names present across the returned rows, in first-seen order. Discovered from the data, not declared." },
                  "rows": { "type": "array", "items": { "$ref": "#/components/schemas/Document" } },
                  "affected": { "type": "integer" },
                  "durationMs": { "type": "number" },
                  "plan": { "type": "string", "description": "How the rows were found." },
                  "page": { "$ref": "#/components/schemas/Page" }
                }
              },
              "Page": {
                "type": "object",
                "description": "Present only when the request asked for a page. 'columns' and 'affected' still describe the whole result, so every page of one query has the same columns.",
                "properties": {
                  "offset": { "type": "integer", "description": "Index of this page's first row in the whole result." },
                  "limit": { "type": "integer" },
                  "returned": { "type": "integer", "description": "Rows on this page." },
                  "total": { "type": "integer", "description": "Rows in the whole result." },
                  "next": { "type": ["string", "null"], "description": "Cursor for the next page, or null on the last one." }
                }
              },
              "Error": {
                "type": "object",
                "properties": { "error": { "type": "string" }, "message": { "type": "string" } }
              }
            },
            "responses": {
              "BadRequest": {
                "description": "The request or the query was not usable. The message is written to be shown to a person.",
                "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/Error" } } }
              },
              "Ndjson": {
                "description": "Sent instead of the JSON object when the request carries Accept: application/x-ndjson. One row per line and nothing else; what the JSON object carries beside the rows travels in headers. The rows are already materialised by the engine; what streams is their serialisation.",
                "headers": {
                  "X-CuteDB-Kind": { "schema": { "type": "string" } },
                  "X-CuteDB-Affected": { "schema": { "type": "integer" } },
                  "X-CuteDB-Duration-Ms": { "schema": { "type": "number" } },
                  "X-CuteDB-Plan": { "schema": { "type": "string" }, "description": "Percent-encoded outside printable ASCII." },
                  "X-CuteDB-Total": { "schema": { "type": "integer" }, "description": "Paged requests only: rows in the whole result." },
                  "X-CuteDB-Next-Cursor": { "schema": { "type": "string" }, "description": "Paged requests only, and absent on the last page." }
                },
                "content": { "application/x-ndjson": { "schema": { "$ref": "#/components/schemas/Document" } } }
              },
              "NotFound": {
                "description": "No such collection, document or index.",
                "content": { "application/problem+json": { "schema": { "$ref": "#/components/schemas/Error" } } }
              }
            }
          },
          "security": [{ "apiKey": [] }, { "bearer": [] }],
          "paths": {
            "/health": {
              "get": {
                "summary": "Liveness check. Never requires an API key.",
                "security": [],
                "responses": { "200": { "description": "The server is up." } }
              }
            },
            "/openapi.json": {
              "get": {
                "summary": "This document.",
                "responses": { "200": { "description": "The OpenAPI description." } }
              }
            },
            "/v1/collections": {
              "get": {
                "summary": "List collections with their sizes.",
                "responses": { "200": { "description": "An array of collection summaries." } }
              }
            },
            "/v1/collections/{collection}": {
              "parameters": [{ "name": "collection", "in": "path", "required": true, "schema": { "type": "string" } }],
              "get": {
                "summary": "One collection's statistics and indexes.",
                "responses": { "200": { "description": "The collection." }, "400": { "$ref": "#/components/responses/BadRequest" } }
              },
              "delete": {
                "summary": "Drop a collection and everything in it.",
                "responses": { "200": { "description": "Dropped." }, "404": { "$ref": "#/components/responses/NotFound" } }
              }
            },
            "/v1/collections/{collection}/documents": {
              "parameters": [{ "name": "collection", "in": "path", "required": true, "schema": { "type": "string" } }],
              "get": {
                "summary": "Page through a collection, optionally filtered.",
                "parameters": [
                  { "name": "filter", "in": "query", "schema": { "type": "string" },
                    "description": "A CuteQL WHERE expression, such as address.city = 'Bandung' AND total > 500000." },
                  { "name": "limit", "in": "query", "schema": { "type": "integer", "default": 100, "maximum": 10000 } },
                  { "name": "offset", "in": "query", "schema": { "type": "integer", "default": 0 } }
                ],
                "responses": { "200": { "description": "A query result, or NDJSON rows when Accept asks for application/x-ndjson.",
                  "content": {
                    "application/json": { "schema": { "$ref": "#/components/schemas/QueryResult" } },
                    "application/x-ndjson": { "schema": { "$ref": "#/components/schemas/Document" } } } } }
              },
              "post": {
                "summary": "Insert one document, or many when the body is an array.",
                "description": "An array is inserted under a single lock and a single flush, which is dramatically faster than one request per document.",
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "oneOf": [
                    { "$ref": "#/components/schemas/Document" },
                    { "type": "array", "items": { "$ref": "#/components/schemas/Document" } }
                  ] } } } },
                "responses": { "201": { "description": "Inserted." }, "400": { "$ref": "#/components/responses/BadRequest" } }
              }
            },
            "/v1/collections/{collection}/documents/{id}": {
              "parameters": [
                { "name": "collection", "in": "path", "required": true, "schema": { "type": "string" } },
                { "name": "id", "in": "path", "required": true, "schema": { "type": "string", "pattern": "^[0-9a-f]{24}$" } }
              ],
              "get": {
                "summary": "Fetch one document by id.",
                "responses": { "200": { "description": "The document." }, "404": { "$ref": "#/components/responses/NotFound" } }
              },
              "put": {
                "summary": "Replace a document wholesale.",
                "description": "The id in the URL wins: a body carrying a different _id has it overwritten.",
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Document" } } } },
                "responses": { "200": { "description": "The stored document." } }
              },
              "patch": {
                "summary": "Merge fields into a document.",
                "description": "A shallow merge. A key containing a dot is treated as a path, so {\"address.city\": \"Bandung\"} reaches into the subdocument while {\"address\": {...}} replaces it.",
                "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Document" } } } },
                "responses": { "200": { "description": "The merged document." }, "404": { "$ref": "#/components/responses/NotFound" } }
              },
              "delete": {
                "summary": "Delete one document.",
                "responses": { "200": { "description": "Deleted." }, "404": { "$ref": "#/components/responses/NotFound" } }
              }
            },
            "/v1/query": {
              "post": {
                "summary": "Run a CuteQL statement.",
                "description": "SELECT, INSERT, UPDATE and DELETE. Bind values through 'parameters' rather than building the statement by concatenation. A SELECT can be paged with 'limit' plus 'offset' or 'cursor'; each page re-runs the statement and returns one slice, so paging bounds the response, not the work the engine does. Send Accept: application/x-ndjson for one row per line.",
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object",
                  "required": ["query"],
                  "properties": {
                    "query": { "type": "string", "example": "SELECT address.city AS city, SUM(total) AS revenue FROM orders GROUP BY address.city" },
                    "parameters": { "type": "object", "additionalProperties": true, "example": { "city": "Bandung" } },
                    "limit": { "type": "integer", "minimum": 1, "maximum": 10000, "default": 100,
                      "description": "Page size. Giving any of limit, offset or cursor asks for a page; SELECT only." },
                    "offset": { "type": "integer", "minimum": 0,
                      "description": "First row of the page. Not together with cursor." },
                    "cursor": { "type": "string",
                      "description": "The 'next' value from the previous page. Valid only with the same query and parameters." }
                  } } } } },
                "responses": { "200": { "description": "A query result. Carries 'page' when one was asked for. With Accept: application/x-ndjson, one row per line instead (see the Ndjson response).",
                  "content": {
                    "application/json": { "schema": { "$ref": "#/components/schemas/QueryResult" } },
                    "application/x-ndjson": { "schema": { "$ref": "#/components/schemas/Document" } } } },
                  "400": { "$ref": "#/components/responses/BadRequest" } }
              }
            },
            "/v1/explain": {
              "post": {
                "summary": "Report how a SELECT would find its rows, without returning them.",
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object", "required": ["query"],
                  "properties": { "query": { "type": "string" }, "parameters": { "type": "object", "additionalProperties": true } } } } } },
                "responses": { "200": { "description": "The plan." }, "400": { "$ref": "#/components/responses/BadRequest" } }
              }
            },
            "/v1/collections/{collection}/indexes": {
              "parameters": [{ "name": "collection", "in": "path", "required": true, "schema": { "type": "string" } }],
              "post": {
                "summary": "Create a secondary index over a document path.",
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object", "required": ["path"],
                  "properties": {
                    "path": { "type": "string", "example": "address.city" },
                    "name": { "type": "string" },
                    "unique": { "type": "boolean", "default": false }
                  } } } } },
                "responses": { "201": { "description": "The index." }, "400": { "$ref": "#/components/responses/BadRequest" } }
              }
            },
            "/v1/collections/{collection}/indexes/{name}": {
              "parameters": [
                { "name": "collection", "in": "path", "required": true, "schema": { "type": "string" } },
                { "name": "name", "in": "path", "required": true, "schema": { "type": "string" } }
              ],
              "delete": {
                "summary": "Drop an index.",
                "responses": { "200": { "description": "Dropped." }, "404": { "$ref": "#/components/responses/NotFound" } }
              }
            },
            "/v1/stats": {
              "get": {
                "summary": "Totals across the database, including how much of the file is history.",
                "responses": { "200": { "description": "Statistics." } }
              }
            },
            "/v1/compact": {
              "post": {
                "summary": "Rewrite the file with only current state, reclaiming space.",
                "responses": { "200": { "description": "Bytes reclaimed." } }
              }
            }
          }
        }
        """;
}
