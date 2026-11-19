map(.address.geo.lat |= . * 2 | .tags += ["x"] | del(.email))
| {tags: (map(.tags | length) | add), lat: (map(.address.geo.lat) | add), keys: (.[0] | keys)}