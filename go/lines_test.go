package jqlite_test

import (
	"strings"
	"testing"

	"github.com/devlooped/jqlite/go"
)

func TestLinesEvaluatesEachValueAlone(t *testing.T) {
	input := strings.NewReader("{\"a\":1}\n\n{\"a\":2}\r\n3\n")
	var seen []string
	for v, err := range jqlite.EvaluateLines(".a? // ., halt, 9", input, nil) {
		if err != nil {
			t.Fatal(err)
		}
		seen = append(seen, jqlite.ToJSON(v))
	}
	same(t, []string{"1", "2", "3"}, seen)
}

func TestLinesStopsAtTheFirstError(t *testing.T) {
	var seen []string
	var last error
	for v, err := range jqlite.EvaluateLines(".a", strings.NewReader("{\"a\":1}\n[1]\n{\"a\":3}\n"), nil) {
		if err != nil {
			last = err
			continue
		}
		seen = append(seen, jqlite.ToJSON(v))
	}
	same(t, []string{"1"}, seen)
	if last == nil || !strings.Contains(last.Error(), "Cannot index array") {
		t.Errorf("%v", last)
	}

	for _, err := range jqlite.EvaluateLines(".", strings.NewReader("1\n{x\n"), nil) {
		if err != nil {
			if !strings.Contains(err.Error(), "line 2") {
				t.Errorf("%v", err)
			}
		}
	}
}

func TestLinesStopsReadingWhenTheLoopBreaks(t *testing.T) {
	count := 0
	for range jqlite.MustParse("range(1000)").Lines(strings.NewReader("1\n2\n"), nil) {
		count++
		if count == 5 {
			break
		}
	}
	if count != 5 {
		t.Errorf("%d", count)
	}
}
