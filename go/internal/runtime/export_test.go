package runtime

// SetHopEvery sets the number of frames a goroutine holds before a hop, for the tests of
// StackGuard (the reference runs them on small threads); the function it gives restores the value.
func SetHopEvery(frames int) (restore func()) {
	saved := hopEvery
	hopEvery = frames
	return func() { hopEvery = saved }
}
