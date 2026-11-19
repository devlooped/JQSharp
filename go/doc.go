// Package jqlite is the Go port of jqlite, the profile of the jq language specified in
// spec/jqlite.md: parse a program once into an Expression, evaluate it many times, from many
// goroutines. This package is the host boundary; the engine is in internal/.
package jqlite
