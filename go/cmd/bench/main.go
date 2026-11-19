// Command bench is the runner of the cross-language benchmark (bench/README.md): the same command
// line, the same three measurements and the same line of output in every port. It drives the core
// engine, so the numbers do not include the host boundary.
//
//	bench <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>
package main

import (
	"fmt"
	"os"
	goruntime "runtime"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/devlooped/jqlite/go/internal/binding"
	_ "github.com/devlooped/jqlite/go/internal/builtins"
	"github.com/devlooped/jqlite/go/internal/runtime"
	"github.com/devlooped/jqlite/go/internal/values"
)

const rounds = 10

func main() { os.Exit(run(os.Args[1:])) }

func run(args []string) int {
	if len(args) != 6 {
		fmt.Fprintln(os.Stderr, "usage: bench <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>")
		return 2
	}

	sourceBytes, err := os.ReadFile(args[0])
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 1
	}
	inputBytes, err := os.ReadFile(args[1])
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 1
	}
	source, inputText := string(sourceBytes), string(inputBytes)
	take, err1 := strconv.Atoi(args[2])
	warmupMs, err2 := strconv.Atoi(args[3])
	measureMs, err3 := strconv.Atoi(args[4])
	if err1 != nil || err2 != nil || err3 != nil {
		fmt.Fprintln(os.Stderr, "take, warmup-ms and measure-ms must be integers")
		return 2
	}

	program, syntaxError := binding.TryCompile(source, true, nil)
	if syntaxError != nil {
		fmt.Fprintf(os.Stderr, "%s at %d:%d\n", syntaxError.Message, syntaxError.Line, syntaxError.Column)
		return 1
	}
	input, inputError, ok := values.ValueText.TryParse(inputText)
	if !ok {
		fmt.Fprintln(os.Stderr, "bad input: "+inputError.String())
		return 1
	}
	options := runtime.NewEvaluationOptions()

	// The check run: the outputs as canonical text, one per line.
	var text strings.Builder
	count := 0
	outcome := runtime.Run(program, input, options, func(v values.Value) bool {
		values.ValueText.Write(&text, v)
		text.WriteByte('\n')
		count++
		return take == 0 || count < take
	})
	if outcome.Kind == runtime.OutcomeError || (outcome.Kind == runtime.OutcomeHalt && outcome.HasValue) {
		fmt.Fprintln(os.Stderr, "error: "+values.ValueText.ToJson(outcome.Value))
		return 1
	}
	if err := os.WriteFile(args[5], []byte(text.String()), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 1
	}

	// compile: the program text to a bound program (the prelude is loaded already).
	compileNs := measure(warmupMs, measureMs, func() { binding.TryCompile(source, true, nil) })

	// eval: the bound program on the input value, outputs collected as values.
	evalNs := measure(warmupMs, measureMs, func() {
		var outputs []values.Value
		runtime.Run(program, input, options, func(v values.Value) bool {
			outputs = append(outputs, v)
			return take == 0 || len(outputs) < take
		})
	})

	// text: the input text read, the program evaluated, the outputs written as text.
	textNs := measure(warmupMs, measureMs, func() {
		v, _, _ := values.ValueText.TryParse(inputText)
		var written strings.Builder
		outputs := 0
		runtime.Run(program, v, options, func(output values.Value) bool {
			values.ValueText.Write(&written, output)
			written.WriteByte('\n')
			outputs++
			return take == 0 || outputs < take
		})
		_ = written.String()
	})

	fmt.Printf("{\"impl\":\"go\",\"runtime\":\"Go %s\",\"compile_ns\":%.1f,\"eval_ns\":%.1f,\"text_ns\":%.1f,\"outputs\":%d}\n",
		strings.TrimPrefix(goruntime.Version(), "go"), compileNs, evalNs, textNs, count)
	return 0
}

// measure gives nanoseconds per operation: the median of ten rounds of equal size, sized from the warm-up.
func measure(warmupMs, measureMs int, operation func()) float64 {
	start := time.Now()
	warmups := 0
	for {
		operation()
		warmups++
		if time.Since(start) >= time.Duration(warmupMs)*time.Millisecond {
			break
		}
	}

	perOperationMs := float64(time.Since(start).Nanoseconds()) / 1e6 / float64(warmups)
	batch := max(1, int64(float64(measureMs)/rounds/perOperationMs))

	results := make([]float64, rounds)
	for round := range results {
		roundStart := time.Now()
		for i := int64(0); i < batch; i++ {
			operation()
		}
		results[round] = float64(time.Since(roundStart).Nanoseconds()) / float64(batch)
	}

	slices.Sort(results)
	return (results[rounds/2-1] + results[rounds/2]) / 2
}
