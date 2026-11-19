[ .[]
  | select(.email | test("@example\\.(com|org)$"))
  | .name
  | ascii_downcase
  | sub("(?<first>\\w+) (?<last>\\w+)"; "\(.last), \(.first)")
]
| unique