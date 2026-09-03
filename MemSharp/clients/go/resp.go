package memsharp

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"strconv"
	"strings"
)

// Error is an error reply from the server.
//
// Code is the leading token, e.g. WRONGTYPE. It is part of the wire contract, so switching on it is
// safe in a way that matching on the message text is not.
type Error struct {
	Code    string
	Message string
}

func (e *Error) Error() string { return e.Code + " " + e.Message }

// IsWrongType reports whether err is a WRONGTYPE reply - a command applied to a key holding a
// different type.
func IsWrongType(err error) bool {
	var replyErr *Error
	return errors.As(err, &replyErr) && replyErr.Code == "WRONGTYPE"
}

// ErrClosed is returned when the connection closed before a complete reply arrived.
var ErrClosed = errors.New("memsharp: connection closed by the server")

// encodeCommand writes a command as a RESP array of bulk strings.
//
// Every argument becomes a bulk string, because RESP has no types on the request side. Floats are
// formatted with -1 precision so they round-trip exactly; a fixed precision would silently change a
// score on its way into a sorted set.
func encodeCommand(builder *strings.Builder, args []any) {
	builder.WriteByte('*')
	builder.WriteString(strconv.Itoa(len(args)))
	builder.WriteString("\r\n")

	for _, arg := range args {
		var payload string
		switch value := arg.(type) {
		case string:
			payload = value
		case []byte:
			payload = string(value)
		case int:
			payload = strconv.Itoa(value)
		case int64:
			payload = strconv.FormatInt(value, 10)
		case float64:
			payload = strconv.FormatFloat(value, 'g', -1, 64)
		case bool:
			if value {
				payload = "1"
			} else {
				payload = "0"
			}
		case nil:
			payload = ""
		default:
			payload = fmt.Sprint(value)
		}

		builder.WriteByte('$')
		builder.WriteString(strconv.Itoa(len(payload)))
		builder.WriteString("\r\n")
		builder.WriteString(payload)
		builder.WriteString("\r\n")
	}
}

// readReply parses one RESP value.
//
// Values are returned as: string for a simple or bulk string, int64 for an integer, []any for an
// array, nil for a null bulk string or array, and *Error for an error reply. An error reply comes
// back as a value rather than as Go's error return, so a pipelined batch can carry one failure per
// position without losing the other replies; the caller decides whether to promote it.
func readReply(reader *bufio.Reader) (any, error) {
	marker, err := reader.ReadByte()
	if err != nil {
		if errors.Is(err, io.EOF) {
			return nil, ErrClosed
		}
		return nil, err
	}

	line, err := readLine(reader)
	if err != nil {
		return nil, err
	}

	switch marker {
	case '+':
		return line, nil

	case '-':
		code, message, found := strings.Cut(line, " ")
		if !found {
			return &Error{Code: "ERR", Message: line}, nil
		}
		return &Error{Code: code, Message: message}, nil

	case ':':
		return strconv.ParseInt(line, 10, 64)

	case '$':
		length, err := strconv.Atoi(line)
		if err != nil {
			return nil, fmt.Errorf("memsharp: malformed bulk length %q: %w", line, err)
		}
		if length < 0 {
			return nil, nil
		}

		// ReadFull rather than Read: a bulk string can span several TCP segments, and a short read
		// would truncate the value and leave the connection desynchronised.
		payload := make([]byte, length+2)
		if _, err := io.ReadFull(reader, payload); err != nil {
			return nil, err
		}
		return string(payload[:length]), nil

	case '*':
		count, err := strconv.Atoi(line)
		if err != nil {
			return nil, fmt.Errorf("memsharp: malformed array length %q: %w", line, err)
		}
		if count < 0 {
			return nil, nil
		}

		items := make([]any, count)
		for i := range items {
			item, err := readReply(reader)
			if err != nil {
				return nil, err
			}
			items[i] = item
		}
		return items, nil

	default:
		return nil, fmt.Errorf("memsharp: unknown RESP type marker %q", marker)
	}
}

// readLine reads up to the next CRLF and returns the content without it.
func readLine(reader *bufio.Reader) (string, error) {
	line, err := reader.ReadString('\n')
	if err != nil {
		if errors.Is(err, io.EOF) {
			return "", ErrClosed
		}
		return "", err
	}
	return strings.TrimSuffix(strings.TrimSuffix(line, "\n"), "\r"), nil
}

// toStrings converts an array reply into a string slice. A null element becomes "".
func toStrings(reply any) []string {
	items, ok := reply.([]any)
	if !ok {
		return nil
	}

	result := make([]string, len(items))
	for i, item := range items {
		if text, ok := item.(string); ok {
			result[i] = text
		}
	}
	return result
}

// toStringPointers converts an array reply into a slice of pointers, so a missing key stays
// distinguishable from an empty string - which MGET needs and a plain []string cannot express.
func toStringPointers(reply any) []*string {
	items, ok := reply.([]any)
	if !ok {
		return nil
	}

	result := make([]*string, len(items))
	for i, item := range items {
		if text, ok := item.(string); ok {
			value := text
			result[i] = &value
		}
	}
	return result
}

// toMap pairs a flat [k, v, k, v] reply into a map.
func toMap(reply any) map[string]string {
	flat := toStrings(reply)
	result := make(map[string]string, len(flat)/2)
	for i := 0; i+1 < len(flat); i += 2 {
		result[flat[i]] = flat[i+1]
	}
	return result
}
