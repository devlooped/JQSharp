//go:build ignore

// Copies spec/prelude.jq next to prelude.go for //go:embed: run with `go generate ./...` from go/.
package main

import "os"

func main() {
	data, err := os.ReadFile("../../../spec/prelude.jq")
	if err != nil {
		panic(err)
	}
	if err := os.WriteFile("prelude.jq", data, 0o644); err != nil {
		panic(err)
	}
}
