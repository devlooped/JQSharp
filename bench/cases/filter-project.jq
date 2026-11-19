[ .[]
  | select(.active and .age >= 30 and any(.tags[]; . == "beta"))
  | {id, name, email, where: .address.geo, tags: (.tags | join(","))}
]
| {count: length, first: .[0], last: .[-1]}