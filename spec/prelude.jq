# jqlite 1.0 prelude: the builtins defined in jqlite itself (spec/jqlite.md §8.3 and §9).
# Every port embeds this file unchanged and binds it before the user's program.
# Natives it relies on: see the N entries of spec/jqlite.md §9.

# §8.3 Assignment
def _assign(paths; $value): reduce path(paths) as $p (.; setpath($p; $value));
def _modify(paths; update):
  reduce path(paths) as $p ([., []];
    . as [$x, $del]
    | label $out
    | (($x | getpath($p) | update) as $v | [($x | setpath($p; $v)), $del] | ., break $out),
      [$x, $del + [$p]])
  | . as [$x, $del] | $x | delpaths($del);

# §9.2 Core
def error($msg): $msg | error;
def not: if . then false else true end;
def env: $ENV;
def halt_error: halt_error(5);
def select(f): if f then . else empty end;
def recurse(f): def r: ., (f | r); r;
def recurse(f; cond): def r: ., (f | select(cond) | r); r;
def recurse: recurse(.[]?);
def isempty(g): [limit(1; g | null)] == [];
def first(f): label $out | (f | ., break $out);
def first: .[0];
def last: .[-1];
def nth($n): .[$n];
def nth($n; f): if $n < 0 then error("nth doesn't support negative indices") else first(skip($n; f)) end;
def have_decnum: false;
def have_literal_numbers: false;
def debug(msgs): (msgs | debug | empty), .;
def input_filename: null;
def input_line_number: 0;

# §9.3 Types and selection
def values: select(. != null);
def nulls: select(. == null);
def booleans: select(type == "boolean");
def numbers: select(type == "number");
def strings: select(type == "string");
def arrays: select(type == "array");
def objects: select(type == "object");
def iterables: select(type | . == "array" or . == "object");
def scalars: select(type | . != "array" and . != "object");
def isfinite: type == "number" and (isinfinite | not);
def finites: select(isfinite);
def normals: select(isnormal);

# §9.4 Objects, arrays and collections
def in(xs): . as $x | xs | has($x);
def inside(xs): . as $x | xs | contains($x);
def map(f): [.[] | f];
def map_values(f): .[] |= f;
def add(f): reduce f as $x (null; . + $x);
def add: add(.[]);
def any(generator; condition): isempty(first(generator | condition or empty)) | not;
def all(generator; condition): isempty(first(generator | condition and empty));
def any(condition): any(.[]; condition);
def all(condition): all(.[]; condition);
def any: any(.);
def all: all(.);
def range($x): range(0; $x);
def range($from; $upto; $by): if $by > 0 then $from | while(. < $upto; . + $by) elif $by < 0 then $from | while(. > $upto; . + $by) else empty end;
def reverse: [.[length - 1 - range(0; length)]];
def sort_by(f): _sort_by_impl(map([f]));
def group_by(f): _group_by_impl(map([f]));
def unique_by(f): [group_by(f)[] | .[0]];
def min_by(f): _min_by_impl(map([f]));
def max_by(f): _max_by_impl(map([f]));
def _flatten($x): reduce .[] as $i ([]; if $i | type == "array" and $x != 0 then . + ($i | _flatten($x - 1)) else . + [$i] end);
def flatten($x): if $x < 0 then error("flatten depth must not be negative") else _flatten($x) end;
def flatten: _flatten(-1);
def to_entries: [keys_unsorted[] as $k | {key: $k, value: .[$k]}];
def from_entries: reduce .[] as $x ({}; . + {($x | .key // .Key // .name // .Name): ($x | if has("value") then .value else .Value end)});
def with_entries(f): to_entries | map(f) | from_entries;
def transpose: [range(0; map(length) | max // 0) as $i | [.[][$i]]];
def combinations: if length == 0 then [] else .[0][] as $x | (.[1:] | combinations) as $w | [$x] + $w end;
def combinations(n): . as $dot | [range(n)] | map($dot) | combinations;
def walk(f): def w: if type == "object" then map_values(w) elif type == "array" then map(w) else . end | f; w;
def INDEX(stream; idx_expr): reduce stream as $row ({}; .[$row | idx_expr | tostring] |= $row);
def INDEX(idx_expr): INDEX(.[]; idx_expr);
def IN(s): any(s == .; .);
def IN(src; s): any(src == s; .);
def JOIN($idx; idx_expr): [.[] | [., $idx[idx_expr]]];
def JOIN($idx; stream; idx_expr): stream | [., $idx[idx_expr]];
def JOIN($idx; stream; idx_expr; join_expr): stream | [., $idx[idx_expr]] | join_expr;

# §9.5 Paths
def paths: path(..) | select(length > 0);
def paths(node_filter): . as $dot | paths | select(. as $p | $dot | getpath($p) | node_filter);
def del(f): delpaths([path(f)]);
def pick(pathexps): . as $top | reduce path(pathexps) as $p (null; setpath($p; $top | getpath($p)));
def tostream: path(def r: (.[]? | r), .; r) as $p | getpath($p) | reduce path(.[]?) as $q ([$p, .]; [$p + $q]);
def fromstream(f): {x: null, e: false} as $init | foreach f as $i ($init; if .e then $init else . end | if $i | length == 2 then setpath(["e"]; $i[0] | length == 0) | setpath(["x"] + $i[0]; $i[1]) else setpath(["e"]; $i[0] | length == 1) end; if .e then .x else empty end);
def truncate_stream(stream): . as $n | null | stream | . as $input | if (.[0] | length) > $n then setpath([0]; $input[0][$n:]) else empty end;

# §9.6 Strings
def ltrimstr($x): if startswith($x) then .[($x | length):] else . end;
def rtrimstr($x): if endswith($x) then .[:length - ($x | length)] else . end;
def trimstr($x): ltrimstr($x) | rtrimstr($x);
def join($x): reduce .[] as $i (null; (if . == null then "" else . + $x end) + ($i | if . == null then "" elif type == "boolean" or type == "number" then tojson else . end)) // "";
def indices($i): if type == "array" and ($i | type) == "array" then .[$i] elif type == "array" then .[[$i]] elif type == "string" and ($i | type) == "string" then _strindices($i) else .[$i] end;
def index($i): indices($i) | .[0];
def rindex($i): indices($i) | .[-1:][0];
def test($val): ($val | type) as $vt | if $vt == "string" then test($val; null) elif $vt == "array" and ($val | length) > 1 then test($val[0]; $val[1]) elif $vt == "array" and ($val | length) > 0 then test($val[0]; null) else error($vt + " not a string or array") end;
def match($val): ($val | type) as $vt | if $vt == "string" then match($val; null) elif $vt == "array" and ($val | length) > 1 then match($val[0]; $val[1]) elif $vt == "array" and ($val | length) > 0 then match($val[0]; null) else error($vt + " not a string or array") end;
def capture(re; mods): match(re; mods) | [.captures[] | select(.name != null) | {key: .name, value: .string}] | from_entries;
def capture($val): ($val | type) as $vt | if $vt == "string" then capture($val; null) elif $vt == "array" and ($val | length) > 1 then capture($val[0]; $val[1]) elif $vt == "array" and ($val | length) > 0 then capture($val[0]; null) else error($vt + " not a string or array") end;
def scan($re; $flags): match($re; "g" + $flags) | if (.captures | length) > 0 then [.captures[].string] else .string end;
def scan($re): scan($re; null);
def split($re; flags): [match($re; "g" + flags) | (.offset, .offset + .length)] as $ms | explode as $cps | [0] + $ms + [$cps | length] | [range(0; length; 2) as $i | $cps[.[$i]:.[$i + 1]] | implode];
def splits($re; flags): split($re; flags) | .[];
def splits($re): splits($re; null);
def sub($re; str; $flags): . as $in
  | [match($re; $flags)] as $edits
  | if $edits == [] then $in
    else (explode as $cps
      | reduce $edits[] as $edit ({result: [], previous: 0};
            ($cps[.previous:($edit | .offset)] | implode) as $gap
            | [$edit | [.captures[] | select(.name != null) | {key: .name, value: .string}] | from_entries | str] as $inserts
            | reduce range(0; $inserts | length) as $ix (.; .result[$ix] += ($gap + $inserts[$ix] | explode))
            | .previous = ($edit | .offset + .length))
      | .result[] + $cps[.previous:] | implode)
      // $in
    end;
def sub($re; str): sub($re; str; "");
def gsub($re; str; $flags): sub($re; str; $flags + "g");
def gsub($re; str): sub($re; str; "g");

# §9.7 Dates
def todateiso8601: strftime("%Y-%m-%dT%H:%M:%SZ");
def fromdateiso8601: strptime("%Y-%m-%dT%H:%M:%SZ") | mktime;
def todate: todateiso8601;
def fromdate: fromdateiso8601;

# §9.8 Math
def abs: if . < 0 then -. else . end;