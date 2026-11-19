// §9.4 contains/1: whether the input contains the argument.

import type { Interpreter } from "../runtime/interpreter.js";
import * as Errors from "../values/errors.js";
import type { JqArray } from "../values/jqArray.js";
import type { JqObject } from "../values/jqObject.js";
import { Fail, JqKind, type JqValue, kindOf } from "../values/jqValue.js";
import * as ValueOps from "../values/valueOps.js";
import { add } from "./nativeTable.js";

export function register(): void {
  add("contains", 1, contains);
}

// At the top level the kinds must be equal; below it a kind mismatch is false.
function contains(_: Interpreter, input: JqValue, args: JqValue[]): JqValue | Fail {
  return kindOf(input) === kindOf(args[0]!) ? check(input, args[0]!) : new Fail(Errors.cannotCheckContainment(input, args[0]!));
}

// A pending check of containers: a contains b. For arrays, every element b[item] must be
// contained by some a[candidate]; for objects, every key of b (b's entry item) must be in a with
// a value that contains b's.
class Frame {
  item = 0;
  candidate = 0;

  constructor(
    readonly a: JqValue,
    readonly b: JqValue,
  ) {}
}

// The result of a step or of scalar(): yes, no, or (for a step) the children to check next.
const enum Answer {
  No,
  Yes,
  Children,
}

// The children a step asks to check.
let childA: JqValue = null;
let childB: JqValue = null;

// Containers are checked with an explicit stack, so that deep values cannot exhaust the host
// stack (§11): each frame asks for one child result at a time, which comes back in 'answer'.
function check(a: JqValue, b: JqValue): boolean {
  const scalar = tryScalar(a, b);
  if (scalar !== Answer.Children) return scalar === Answer.Yes;

  const stack: Frame[] = [new Frame(a, b)];
  let answer = false;
  let answered = false;
  while (stack.length > 0) {
    const frame = stack[stack.length - 1]!;
    const done: Answer = kindOf(frame.b) === JqKind.Array ? stepArray(frame, answered, answer) : stepObject(frame, answered, answer);
    answered = false;
    if (done !== Answer.Children) {
      stack.pop();
      answer = done === Answer.Yes;
      answered = true;
      continue;
    }

    const direct: Answer = tryScalar(childA, childB);
    if (direct !== Answer.Children) {
      answer = direct === Answer.Yes;
      answered = true;
    } else {
      stack.push(new Frame(childA, childB));
    }
  }

  return answer;
}

// The next child check of an array frame (in childA, childB), or its result.
function stepArray(frame: Frame, answered: boolean, answer: boolean): Answer {
  const a = frame.a as JqArray;
  const b = frame.b as JqArray;
  if (answered) {
    if (answer) {
      frame.item++;
      frame.candidate = 0;
    } else {
      frame.candidate++;
    }
  }

  if (frame.item === b.count) return Answer.Yes;
  if (frame.candidate === a.count) return Answer.No;
  childA = a.get(frame.candidate);
  childB = b.get(frame.item);
  return Answer.Children;
}

// The next child check of an object frame (in childA, childB), or its result.
function stepObject(frame: Frame, answered: boolean, answer: boolean): Answer {
  const a = frame.a as JqObject;
  const b = frame.b as JqObject;
  if (answered) {
    if (!answer) return Answer.No;
    frame.item++;
  }

  if (frame.item === b.count) return Answer.Yes;
  const value = a.get(b.keyAt(frame.item));
  if (value === undefined) return Answer.No;
  childA = value;
  childB = b.valueAt(frame.item);
  return Answer.Children;
}

// The result of a check that needs no frame: kinds differ, strings, or other scalars; Children for two containers.
function tryScalar(a: JqValue, b: JqValue): Answer {
  const kind = kindOf(a);
  if (kind !== kindOf(b)) return Answer.No;
  switch (kind) {
    case JqKind.Array:
    case JqKind.Object:
      return Answer.Children;
    case JqKind.String:
      return (a as string).includes(b as string) ? Answer.Yes : Answer.No;
    default:
      return ValueOps.areEqual(a, b) ? Answer.Yes : Answer.No;
  }
}
