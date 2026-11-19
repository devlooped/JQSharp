package runtime

// Host-specific (H): what to do when the host stack runs short. Spec §11 lets a program observe no
// recursion limit but MaxDepth, and a port must not crash below it. A goroutine's stack grows, but
// only up to runtime/debug.SetMaxStack (1 GB on 64-bit systems, 250 MB on 32-bit ones), and going
// past it is a fatal error that no recover catches. How much stack a call level takes depends on
// the program: about 1–1.6 KB for a plain recursion, but a recursive call written inside a deeply
// nested body costs that nesting at every level. So, as the reference does with threads, the
// interpreter counts the Eval and EvalOne frames active on the current goroutine, and when they
// reach HopEvery the step continues on a new goroutine, whose stack starts empty, while the current
// one waits. The stack of each goroutine stays bounded (HopEvery frames, a few KB each at most),
// whatever MaxDepth is; what remains is the memory of all of them.
//
// The reference (.NET) checks the stack itself (RuntimeHelpers.TryEnsureSufficientExecutionStack)
// and runs the step on a thread with a 16 MB stack; Rust uses stacker::maybe_grow, Java and Python a
// thread with a chosen stack size, JavaScript a lower MaxDepth.

type stackGuard struct{}

// StackGuard decides when a step continues on a fresh stack, and runs it there.
var StackGuard stackGuard

// hopEvery is the number of Eval and EvalOne frames one goroutine holds before the next continues
// on a new goroutine. A frame of Eval or EvalOne and the helpers and callbacks up to the next one
// take a few hundred bytes, so 10,000 of them stay far below Go's maximum stack.
var hopEvery = 10000

// HasStack tells whether the current stack has room for a step: fewer than hopEvery frames.
func (stackGuard) HasStack(frames int) bool { return frames < hopEvery }

// OnFreshStack runs step on a new goroutine and waits for it. A panic of the step (a defect, or a
// panic of the host's callback) is carried back and raised again on the caller's goroutine.
func (stackGuard) OnFreshStack(step func()) {
	var failure any
	failed := false
	done := make(chan struct{})
	go func() {
		defer func() {
			if r := recover(); r != nil {
				failure, failed = r, true
			}
			close(done)
		}()
		step()
	}()
	<-done
	if failed {
		panic(failure)
	}
}
