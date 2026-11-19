package jqlite

import (
	"bufio"
	"fmt"
	"io"
	"iter"
	"strings"
)

// maxLine is the longest line Lines reads.
const maxLine = 1 << 30

// Lines evaluates the expression on each value of a JSON Lines stream in turn (one JSON value per
// line, blank lines skipped), yielding the results of each; the results of one input are computed
// before they are yielded. Each value is evaluated alone (spec §12): halt ends only the evaluation
// of its value. A line that is not JSON, an error that reaches the top of the expression or an
// error of the reader is yielded with Null and ends the sequence; breaking out of the loop stops
// reading.
func (e *Expression) Lines(r io.Reader, variables map[string]Value) iter.Seq2[Value, error] {
	return func(yield func(Value, error) bool) {
		scanner := bufio.NewScanner(r)
		scanner.Buffer(make([]byte, 64*1024), maxLine)
		number := 0
		for scanner.Scan() {
			number++
			text := strings.TrimSpace(scanner.Text())
			if text == "" {
				continue
			}
			input, err := ParseJSON(text)
			if err != nil {
				yield(Null, fmt.Errorf("jqlite: line %d: %w", number, err))
				return
			}
			results, err := e.Evaluate(input, variables)
			if err != nil {
				yield(Null, err)
				return
			}
			for _, result := range results {
				if !yield(result, nil) {
					return
				}
			}
		}
		if err := scanner.Err(); err != nil {
			yield(Null, err)
		}
	}
}

// EvaluateLines parses expression with the default options and evaluates it on each value of a
// JSON Lines stream, as Expression.Lines; a parse error is the first and only element.
func EvaluateLines(expression string, r io.Reader, variables map[string]Value) iter.Seq2[Value, error] {
	e, err := Parse(expression)
	if err != nil {
		return func(yield func(Value, error) bool) { yield(Null, err) }
	}
	return e.Lines(r, variables)
}
