package values

import "sync/atomic"

// jqString is an immutable string made by jq's + on strings, which later appends extend in place.
//
// Storage is a shared buffer of UTF-8 bytes plus a length, as Array is without its start. A byte
// of a buffer is written once, before any string that covers it exists, and never again, so every
// jqString is immutable however many strings share its buffer.
//
// Appending (Append) to the string whose length is the buffer's used part (its latest version)
// claims the next free bytes with one compare-and-swap on used and writes there: amortized
// O(length of the appended text), so reduce range(n) as $i (""; . + "ab"), and with it add and
// join, is O(n). When the bytes are already taken (an older version, or another goroutine won the
// claim) or the buffer is full, the string is copied to a new buffer with twice the room. Text
// builds the Go string once per version, when a reader needs it.
type jqString struct {
	buffer *stringBuffer
	length int
	// The Go string of this version, built on first read. Two goroutines may both build it: they
	// build equal strings, and either may be kept.
	text atomic.Pointer[string]
}

type stringBuffer struct {
	bytes []byte
	// Bytes [0, used) are claimed. Only the goroutine that claimed a byte writes it, before it
	// publishes the string that covers it.
	used atomic.Int64
}

// stringMinLength is the shortest concatenation that gets a buffer: shorter ones are plain Go
// strings, whose copies cost little and which need no second copy for Text.
const stringMinLength = 256

func newStringBuffer(bytes []byte, used int) *stringBuffer {
	b := &stringBuffer{bytes: bytes}
	b.used.Store(int64(used))
	return b
}

// newJqString gives left followed by right, in a new buffer with room to append.
func newJqString(left, right string) *jqString {
	total := len(left) + len(right)
	bytes := make([]byte, growString(total))
	copy(bytes, left)
	copy(bytes[len(left):], right)
	return &jqString{buffer: newStringBuffer(bytes, total), length: total}
}

// Text gives the Go string, built once per version.
func (s *jqString) Text() string {
	if text := s.text.Load(); text != nil {
		return *text
	}
	text := string(s.buffer.bytes[:s.length])
	s.text.Store(&text)
	return text
}

// Append gives this string followed by right.
func (s *jqString) Append(right string) *jqString {
	total := s.length + len(right)
	if total <= len(s.buffer.bytes) && s.buffer.used.CompareAndSwap(int64(s.length), int64(total)) {
		copy(s.buffer.bytes[s.length:], right)
		return &jqString{buffer: s.buffer, length: total}
	}

	bytes := make([]byte, growString(total))
	copy(bytes, s.buffer.bytes[:s.length])
	copy(bytes[s.length:], right)
	return &jqString{buffer: newStringBuffer(bytes, total), length: total}
}

// growString gives twice the room, within the longest string of the spec (§11); past it the
// allocation is what it needs.
func growString(needed int) int { return max(needed, min(needed*2, maxStringBytes)) }
