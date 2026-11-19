// §9.8 Math. One-input functions take a number input; multi-input ones ignore the input and take
// number arguments (the first non-number, in argument order, is the error).

import * as Errors from "../values/errors.js";
import { JqArray } from "../values/jqArray.js";
import { Fail, type JqValue } from "../values/jqValue.js";
import * as MathExtra from "./mathExtra.js";
import { add } from "./nativeTable.js";

// The functions of one number: the exactly specified ones, the library functions (the host's
// Math, which has all of them, cbrt and hypot included), and those the spec defines by formula
// (MathExtra: exp2, expm1, log1p and the special functions).
const unary: readonly (readonly [name: string, func: (x: number) => number])[] = [
  ["floor", Math.floor],
  ["ceil", Math.ceil],
  ["trunc", Math.trunc],
  ["round", MathExtra.round],
  ["nearbyint", MathExtra.nearbyInt],
  ["rint", MathExtra.nearbyInt],
  ["fabs", Math.abs],
  ["sqrt", Math.sqrt],
  ["logb", MathExtra.logb],
  ["significand", MathExtra.significand],
  ["acos", Math.acos],
  ["asin", Math.asin],
  ["atan", Math.atan],
  ["acosh", Math.acosh],
  ["asinh", Math.asinh],
  ["atanh", Math.atanh],
  ["cbrt", Math.cbrt],
  ["cos", Math.cos],
  ["cosh", Math.cosh],
  ["exp", Math.exp],
  ["exp2", MathExtra.exp2],
  ["exp10", MathExtra.exp10],
  ["expm1", MathExtra.expm1],
  ["log", Math.log],
  ["log10", Math.log10],
  ["log1p", MathExtra.log1p],
  ["log2", Math.log2],
  ["sin", Math.sin],
  ["sinh", Math.sinh],
  ["tan", Math.tan],
  ["tanh", Math.tanh],
  ["erf", MathExtra.erf],
  ["erfc", MathExtra.erfc],
  ["tgamma", MathExtra.tgamma],
  ["lgamma", MathExtra.lgamma],
  ["gamma", MathExtra.lgamma],
  ["j0", MathExtra.j0],
  ["j1", MathExtra.j1],
  ["y0", MathExtra.y0],
  ["y1", MathExtra.y1],
];

const binary: readonly (readonly [name: string, func: (x: number, y: number) => number])[] = [
  ["fmod", (x, y) => x % y],
  ["remainder", MathExtra.remainder],
  ["drem", MathExtra.remainder],
  ["fmin", MathExtra.min],
  ["fmax", MathExtra.max],
  ["fdim", MathExtra.fdim],
  ["copysign", MathExtra.copySign],
  ["nextafter", MathExtra.nextAfter],
  ["nexttoward", MathExtra.nextAfter],
  ["ldexp", MathExtra.scaleB],
  ["scalbln", MathExtra.scaleB],
  ["scalb", MathExtra.scalb],
  ["atan2", Math.atan2],
  ["pow", MathExtra.pow],
  ["hypot", (x, y) => Math.hypot(x, y)],
  ["jn", MathExtra.jn],
  ["yn", MathExtra.yn],
];

export function register(): void {
  for (const [name, func] of unary) {
    add(name, 0, (_, input) => (typeof input === "number" ? func(input) : new Fail(Errors.numberRequired(input))));
  }

  for (const [name, func] of binary) {
    add(name, 2, (_, _input, args) => numbers(args) ?? func(args[0] as number, args[1] as number));
  }

  add("fma", 3, (_, _input, args) => numbers(args) ?? MathExtra.fma(args[0] as number, args[1] as number, args[2] as number));
  add("frexp", 0, (_, input) => {
    if (typeof input !== "number") return new Fail(Errors.numberRequired(input));
    const [mantissa, exponent] = MathExtra.frexp(input);
    return JqArray.of(mantissa, exponent);
  });
  add("lgamma_r", 0, (_, input) => {
    if (typeof input !== "number") return new Fail(Errors.numberRequired(input));
    return JqArray.of(MathExtra.lgamma(input), MathExtra.gammaSign(input));
  });
  add("modf", 0, (_, input) => {
    if (typeof input !== "number") return new Fail(Errors.numberRequired(input));
    const [fraction, integer] = MathExtra.modf(input);
    return JqArray.of(fraction, integer);
  });
}

// The error of the first argument that is not a number, or null.
function numbers(args: JqValue[]): Fail | null {
  for (const argument of args) {
    if (typeof argument !== "number") return new Fail(Errors.numberRequired(argument));
  }

  return null;
}
