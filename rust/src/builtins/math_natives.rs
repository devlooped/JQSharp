//! §9.8 Math. One-input functions take a number input; multi-input ones ignore the input and take
//! number arguments (the first non-number, in argument order, is the error).
//!
//! Host-specific: the library functions of §9.8 (`acos` … `tanh`, `atan2`, `pow`, `hypot`, and
//! `fma` as `mul_add`) are the methods of `f64`, which call the platform's C library; `fmod` is
//! the `%` operator. Everything else is `math_extra`, which is portable code.

use crate::values::{JqArray, JqValue, errors};

use super::math_extra;
use super::native_table::Registry;

// A function of one number: the input must be a number.
macro_rules! unary {
    ($table:expr, $name:literal, $function:expr) => {
        $table.value($name, 0, |_, input, _| match &*input {
            JqValue::Number(x) => {
                let function: fn(f64) -> f64 = $function;
                Ok(JqValue::Number(function(*x)))
            }
            other => Err(errors::number_required(other)),
        })
    };
}

// A function of two numbers: the arguments must be numbers.
macro_rules! binary {
    ($table:expr, $name:literal, $function:expr) => {
        $table.value($name, 2, |_, _, arguments| {
            let function: fn(f64, f64) -> f64 = $function;
            let (x, y) = (number(&arguments[0])?, number(&arguments[1])?);
            Ok(JqValue::Number(function(x, y)))
        })
    };
}

pub fn register(table: &mut Registry) {
    // The functions of one number: the exactly specified ones, the library functions (the host's
    // libm, cbrt and hypot included), and those the spec defines by formula (math_extra: exp2,
    // expm1, log1p and the special functions).
    unary!(table, "floor", f64::floor);
    unary!(table, "ceil", f64::ceil);
    unary!(table, "trunc", f64::trunc);
    unary!(table, "round", math_extra::round);
    unary!(table, "nearbyint", math_extra::nearby_int);
    unary!(table, "rint", math_extra::nearby_int);
    unary!(table, "fabs", f64::abs);
    unary!(table, "sqrt", f64::sqrt);
    unary!(table, "logb", math_extra::logb);
    unary!(table, "significand", math_extra::significand);
    unary!(table, "acos", f64::acos);
    unary!(table, "asin", f64::asin);
    unary!(table, "atan", f64::atan);
    unary!(table, "acosh", f64::acosh);
    unary!(table, "asinh", f64::asinh);
    unary!(table, "atanh", f64::atanh);
    unary!(table, "cbrt", f64::cbrt);
    unary!(table, "cos", f64::cos);
    unary!(table, "cosh", f64::cosh);
    unary!(table, "exp", f64::exp);
    unary!(table, "exp2", math_extra::exp2);
    unary!(table, "exp10", math_extra::exp10);
    unary!(table, "expm1", math_extra::expm1);
    unary!(table, "log", f64::ln);
    unary!(table, "log10", f64::log10);
    unary!(table, "log1p", math_extra::log1p);
    unary!(table, "log2", f64::log2);
    unary!(table, "sin", f64::sin);
    unary!(table, "sinh", f64::sinh);
    unary!(table, "tan", f64::tan);
    unary!(table, "tanh", f64::tanh);
    unary!(table, "erf", math_extra::erf);
    unary!(table, "erfc", math_extra::erfc);
    unary!(table, "tgamma", math_extra::tgamma);
    unary!(table, "lgamma", math_extra::lgamma);
    unary!(table, "gamma", math_extra::lgamma);
    unary!(table, "j0", math_extra::j0);
    unary!(table, "j1", math_extra::j1);
    unary!(table, "y0", math_extra::y0);
    unary!(table, "y1", math_extra::y1);

    binary!(table, "fmod", |x, y| x % y);
    binary!(table, "remainder", math_extra::remainder);
    binary!(table, "drem", math_extra::remainder);
    binary!(table, "fmin", math_extra::min);
    binary!(table, "fmax", math_extra::max);
    binary!(table, "fdim", math_extra::fdim);
    binary!(table, "copysign", math_extra::copy_sign);
    binary!(table, "nextafter", math_extra::next_after);
    binary!(table, "nexttoward", math_extra::next_after);
    binary!(table, "ldexp", math_extra::scale_b);
    binary!(table, "scalbln", math_extra::scale_b);
    binary!(table, "scalb", math_extra::scalb);
    binary!(table, "atan2", f64::atan2);
    binary!(table, "pow", f64::powf);
    binary!(table, "hypot", f64::hypot);
    binary!(table, "jn", math_extra::jn);
    binary!(table, "yn", math_extra::yn);

    table.value("fma", 3, |_, _, arguments| {
        let (x, y, z) = (number(&arguments[0])?, number(&arguments[1])?, number(&arguments[2])?);
        Ok(JqValue::Number(x.mul_add(y, z)))
    });
    table.value("frexp", 0, |_, input, _| {
        let (mantissa, exponent) = math_extra::frexp(number(&input)?);
        Ok(pair(mantissa, exponent))
    });
    table.value("lgamma_r", 0, |_, input, _| {
        let x = number(&input)?;
        Ok(pair(math_extra::lgamma(x), math_extra::gamma_sign(x)))
    });
    table.value("modf", 0, |_, input, _| {
        let (fraction, integer) = math_extra::modf(number(&input)?);
        Ok(pair(fraction, integer))
    });
}

// "num req" (§9.1).
fn number(value: &JqValue) -> Result<f64, JqValue> {
    match value {
        JqValue::Number(number) => Ok(*number),
        other => Err(errors::number_required(other)),
    }
}

fn pair(first: f64, second: f64) -> JqValue {
    JqValue::Array(JqArray::from_owned(vec![JqValue::Number(first), JqValue::Number(second)]))
}
