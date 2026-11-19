package com.devlooped.jqlite.builtins;

import com.devlooped.jqlite.builtins.Native.NativeGroup;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;

/**
 * The native catalogue: every builtin that spec §9 marks N, in the order of the spec. With the
 * definitions of {@code spec/prelude.jq} it is the whole builtin set, and what {@code builtins/0} lists.
 */
public final class Natives {
    private Natives() {
    }

    private record Single(String signature, int[] arguments) {
    }

    // §7.7: the natives that take closures and yet give at most one output, when these arguments
    // are single (none: always). The two overrides are single when their prelude definitions are.
    // Before ALL: static fields are set in this order.
    private static final Single[] SINGLES = {
        new Single("empty/0", new int[0]), new Single("halt/0", new int[0]), new Single("halt_error/1", new int[0]),
        new Single("last/1", new int[0]), new Single("getpath/1", new int[] {0}), new Single("until/2", new int[] {0, 1}),
        new Single("_modify/2", new int[0]), new Single("_assign/2", new int[] {1}),
    };

    public static final List<Native> ALL = withOverrides(new String[] {"_modify/2", "_assign/2"}, build(
        new String[] {
            // §9.2 Core
            "empty/0", "error/0", "type/0", "length/0", "utf8bytelength/0", "builtins/0", "halt/0", "halt_error/1", "last/1",
            "debug/0", "stderr/0",
            // §9.3 Types and selection
            "infinite/0", "nan/0", "isinfinite/0", "isnan/0", "isnormal/0",
            // §9.4 Objects, arrays and collections
            "keys/0", "keys_unsorted/0", "has/1", "contains/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
            "sort/0", "_sort_by_impl/1", "_group_by_impl/1", "_min_by_impl/1", "_max_by_impl/1", "unique/0", "min/0", "max/0", "bsearch/1",
            // §9.5 Paths
            "path/1", "getpath/1", "setpath/2", "delpaths/1",
            // §9.6 Strings
            "tostring/0", "tojson/0", "fromjson/0", "tonumber/0", "toboolean/0", "format/1", "explode/0", "implode/0", "ascii_downcase/0", "ascii_upcase/0",
            "startswith/1", "endswith/1", "trim/0", "ltrim/0", "rtrim/0", "split/1", "_strindices/1", "test/2", "match/2",
            // §9.7 Dates
            "now/0", "gmtime/0", "localtime/0", "mktime/0", "strftime/1", "strflocaltime/1", "strptime/1",
            // §9.8 Math: exactly specified, library functions, defined by formula
            "floor/0", "ceil/0", "trunc/0", "round/0", "nearbyint/0", "rint/0", "fabs/0", "sqrt/0", "fmod/2", "remainder/2", "drem/2", "fma/3",
            "fmin/2", "fmax/2", "fdim/2", "copysign/2", "nextafter/2", "nexttoward/2", "ldexp/2", "scalbln/2", "scalb/2", "logb/0",
            "significand/0", "frexp/0", "modf/0",
            "acos/0", "asin/0", "atan/0", "acosh/0", "asinh/0", "atanh/0", "cbrt/0", "cos/0", "cosh/0", "exp/0", "exp2/0",
            "exp10/0", "expm1/0", "log/0", "log10/0", "log1p/0", "log2/0", "sin/0", "sinh/0", "tan/0", "tanh/0", "atan2/2", "pow/2", "hypot/2",
            "erf/0", "erfc/0", "tgamma/0", "lgamma/0", "gamma/0", "lgamma_r/0", "j0/0", "j1/0", "y0/0", "y1/0", "jn/2", "yn/2",
        },
        new String[] {
            // §9.11 Modules
            "modulemeta/0",
        },
        new String[] {
            // §9.2, §9.4, §9.5, §9.6: no output, a signal, generators, path-capable, many outputs.
            "empty/0", "halt/0", "halt_error/1", "last/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
            "path/1", "getpath/1", "match/2",
        }));

    private static final HashMap<String, Native> BY_KEY = index(ALL);

    /** The native {@code name}/{@code arity}, or null. */
    public static Native find(String name, int arity) {
        return BY_KEY.get(key(name, arity));
    }

    /** The native that overrides the prelude builtin {@code name}/{@code arity}, or null. */
    public static Native override(String name, int arity) {
        Native builtin = find(name, arity);
        return builtin != null && builtin.overridesPrelude() ? builtin : null;
    }

    private static String key(String name, int arity) {
        return name + "/" + arity;
    }

    // The overrides of prelude builtins, after the natives of §9; they take closures.
    private static List<Native> withOverrides(String[] overrides, List<Native> natives) {
        List<Native> all = new ArrayList<>(natives);
        for (String signature : overrides) {
            int slash = signature.indexOf('/');
            all.add(new Native(signature.substring(0, slash), Integer.parseInt(signature.substring(slash + 1)), NativeGroup.CORE, all.size(), true, true, singleWhen(signature, true)));
        }

        return List.copyOf(all);
    }

    private static List<Native> build(String[] core, String[] modules, String[] closures) {
        List<Native> natives = new ArrayList<>(core.length + modules.length);
        add(natives, core, NativeGroup.CORE, closures);
        add(natives, modules, NativeGroup.MODULES, closures);
        return natives;
    }

    private static void add(List<Native> natives, String[] signatures, NativeGroup group, String[] closures) {
        List<String> closureList = Arrays.asList(closures);
        for (String signature : signatures) {
            int slash = signature.indexOf('/');
            boolean takesClosures = closureList.contains(signature);
            natives.add(new Native(signature.substring(0, slash), Integer.parseInt(signature.substring(slash + 1)), group, natives.size(), takesClosures, false, singleWhen(signature, takesClosures)));
        }
    }

    private static int[] singleWhen(String signature, boolean takesClosures) {
        if (takesClosures) {
            for (Single single : SINGLES) {
                if (single.signature.equals(signature)) {
                    return single.arguments.clone();
                }
            }

            return null;
        }

        int[] all = new int[Integer.parseInt(signature.substring(signature.indexOf('/') + 1))];
        for (int slot = 0; slot < all.length; slot++) {
            all[slot] = slot;
        }
        return all;
    }

    private static HashMap<String, Native> index(List<Native> natives) {
        HashMap<String, Native> index = new HashMap<>(natives.size() * 2);
        for (Native builtin : natives) {
            index.put(key(builtin.name(), builtin.arity()), builtin);
        }
        return index;
    }
}
